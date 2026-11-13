using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Skimr.Infrastructure.Fetching;

internal sealed record FetchRequest(Uri Uri, string Accept, string? ETag = null, string? LastModified = null);

internal sealed record FetchResponse(
    HttpStatusCode Status,
    Uri FinalUri,
    byte[] Body,
    string? ContentType,
    string? ETag,
    string? LastModified,
    TimeSpan? MaxAge,
    TimeSpan? RetryAfter,
    string? HubUrl,
    string? SelfUrl)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
}

/// <summary>A fetch that failed before an HTTP status was available. The message is shown to users.</summary>
internal sealed class FetchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Outbound HTTP. An interface only because the refresher and discovery tests need a fake.</summary>
internal interface IOutboundHttp
{
    Task<FetchResponse> GetAsync(FetchRequest request, CancellationToken cancellationToken);

    Task<FetchResponse> PostFormAsync(Uri uri, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken);
}

internal sealed partial class OutboundHttp(IHttpClientFactory clientFactory, FetchOptions options, TimeProvider time) : IOutboundHttp
{
    public const string ClientName = "outbound";

    public Task<FetchResponse> GetAsync(FetchRequest request, CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, request.Uri);
        message.Headers.TryAddWithoutValidation("Accept", request.Accept);
        if (request.ETag is not null)
        {
            message.Headers.TryAddWithoutValidation("If-None-Match", request.ETag);
        }

        if (request.LastModified is not null)
        {
            message.Headers.TryAddWithoutValidation("If-Modified-Since", request.LastModified);
        }

        return SendAsync(message, cancellationToken);
    }

    public Task<FetchResponse> PostFormAsync(Uri uri, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) }, cancellationToken);

    private async Task<FetchResponse> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        using var _ = message;
        if (message.RequestUri is not { IsAbsoluteUri: true, Scheme: "http" or "https" } uri)
        {
            throw new FetchException($"Only http and https URLs can be fetched (got '{message.RequestUri}').");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        try
        {
            using var client = clientFactory.CreateClient(ClientName);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if ((int)response.StatusCode is >= 300 and < 400 && response.StatusCode != HttpStatusCode.NotModified)
            {
                throw new FetchException(options.MaxRedirects == 0
                    ? $"Redirected (HTTP {(int)response.StatusCode}) and redirects are disabled."
                    : $"Too many redirects or an https-to-http redirect (HTTP {(int)response.StatusCode}).");
            }

            var body = await ReadLimitedAsync(response.Content, timeout.Token);
            var links = response.Headers.TryGetValues("Link", out var linkValues) ? string.Join(",", linkValues) : "";

            return new FetchResponse(
                Status: response.StatusCode,
                FinalUri: response.RequestMessage?.RequestUri ?? uri,
                Body: body,
                ContentType: response.Content.Headers.ContentType?.MediaType,
                ETag: response.Headers.ETag?.ToString(),
                LastModified: response.Content.Headers.LastModified?.ToString("R"),
                MaxAge: response.Headers.CacheControl?.MaxAge,
                RetryAfter: RetryAfter(response.Headers.RetryAfter),
                HubUrl: LinkRel(links, "hub"),
                SelfUrl: LinkRel(links, "self"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FetchException($"Timed out after {options.Timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            throw new FetchException(Describe(ex, uri), ex);
        }
    }

    private async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var max = options.MaxResponseBytes;
        if (content.Headers.ContentLength > max)
        {
            throw new FetchException($"Response is larger than the {max / (1024 * 1024)} MB limit.");
        }

        // Counted after decompression, so a small gzip bomb can't get past the limit.
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > max)
            {
                throw new FetchException($"Response is larger than the {max / (1024 * 1024)} MB limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private TimeSpan? RetryAfter(RetryConditionHeaderValue? header) => header switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - time.GetUtcNow() is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
        _ => null,
    };

    private string Describe(HttpRequestException ex, Uri uri)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SsrfBlockedException blocked:
                    return options.AllowPrivateNetworks
                        ? $"Blocked: {uri.Host} resolves to {blocked.Address}, a cloud metadata or reserved address."
                        : $"Blocked: {uri.Host} resolves to {blocked.Address}, a private or reserved address. Set Fetch:AllowPrivateNetworkFeeds to fetch from your own network.";
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }:
                    return $"DNS lookup for {uri.Host} failed.";
                case SocketException socket:
                    return $"Could not connect to {uri.Host}: {socket.SocketErrorCode}.";
            }
        }

        return $"Request to {uri.Host} failed: {ex.Message}";
    }

    private static string? LinkRel(string header, string rel)
    {
        foreach (Match m in LinkHeader().Matches(header))
        {
            var rels = m.Groups["rel"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rels.Contains(rel, StringComparer.OrdinalIgnoreCase))
            {
                return m.Groups["url"].Value;
            }
        }

        return null;
    }

    [GeneratedRegex("""<(?<url>[^>]+)>[^,]*?;\s*rel\s*=\s*"?(?<rel>[^";,]+)"?""")]
    private static partial Regex LinkHeader();
}
