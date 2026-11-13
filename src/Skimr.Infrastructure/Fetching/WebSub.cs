using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Skimr.Domain;
using Skimr.Infrastructure.Feeds;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Fetching;

/// <summary>
/// WebSub (PubSubHubbub) subscriber. Only active when Server:PublicUrl is set, because the hub
/// must be able to reach our callback (PLAN D17). Polling carries on regardless as a safety net.
/// </summary>
internal sealed partial class WebSub(
    SkimrDbContext db,
    IOutboundHttp http,
    ArticleIngester ingester,
    FetchOptions options,
    TimeProvider time,
    ILogger<WebSub> logger)
{
    public const string CallbackPrefix = "/websub/";

    private static readonly TimeSpan LeaseRequested = TimeSpan.FromDays(10);
    private static readonly TimeSpan MaxLease = TimeSpan.FromDays(30);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(1);
    private static readonly TimeSpan RetryUnverified = TimeSpan.FromHours(1);

    /// <summary>Called after a successful fetch. Subscribes, or renews a lease that's about to run out.</summary>
    public async Task EnsureSubscribedAsync(Feed feed, CancellationToken cancellationToken)
    {
        // The secret travels in the subscribe request, so only send it over https (WebSub spec).
        if (options.PublicUrl is null || feed.SelfUrl is null
            || feed.HubUrl is not { } hub || !hub.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (feed.WebSubLeaseExpiresAt is { } lease && lease - now > RenewBefore)
        {
            return;
        }

        feed.WebSubSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        // Provisional: if the hub never verifies, this makes us try again in an hour, not every fetch.
        feed.WebSubLeaseExpiresAt = now + RenewBefore + RetryUnverified;

        var form = new Dictionary<string, string>
        {
            ["hub.mode"] = "subscribe",
            ["hub.topic"] = feed.SelfUrl,
            ["hub.callback"] = CallbackUrl(feed.Id),
            ["hub.secret"] = feed.WebSubSecret,
            ["hub.lease_seconds"] = ((int)LeaseRequested.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        try
        {
            var response = await http.PostFormAsync(new Uri(feed.HubUrl), form, cancellationToken);
            if (!response.IsSuccess)
            {
                LogHubRefused(feed.Id, (int)response.Status);
            }
        }
        catch (FetchException ex)
        {
            LogHubFailed(feed.Id, ex.Message);
        }
    }

    public string CallbackUrl(long feedId) => $"{options.PublicUrl!.AbsoluteUri.TrimEnd('/')}{CallbackPrefix}{feedId}";

    /// <summary>Hub's GET to confirm intent. Returns the challenge to echo, or null to answer 404.</summary>
    public async Task<string?> VerifyAsync(long feedId, string? mode, string? topic, string? challenge, string? leaseSeconds, CancellationToken cancellationToken)
    {
        var feed = await db.Feeds.SingleOrDefaultAsync(f => f.Id == feedId, cancellationToken);
        if (feed is null || string.IsNullOrEmpty(challenge) || topic is null || topic != feed.SelfUrl)
        {
            return null;
        }

        var now = time.GetUtcNow().UtcDateTime;
        switch (mode)
        {
            case "subscribe" when feed.WebSubSecret is not null:
                var lease = int.TryParse(leaseSeconds, out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : LeaseRequested;
                feed.WebSubLeaseExpiresAt = now + (lease > MaxLease ? MaxLease : lease);
                await db.SaveChangesAsync(cancellationToken);
                return challenge;
            case "unsubscribe" when !await db.Subscriptions.AnyAsync(s => s.FeedId == feedId, cancellationToken):
                return challenge;
            case "denied":
                feed.WebSubLeaseExpiresAt = null;
                feed.WebSubSecret = null;
                await db.SaveChangesAsync(cancellationToken);
                return "";
            default:
                return null;
        }
    }

    /// <summary>
    /// Hub's POST with new content. Unsigned or wrongly signed content is ignored, but the hub
    /// still gets a 2xx, as the spec requires. Returns whether the content was accepted.
    /// </summary>
    public async Task<bool> DeliverAsync(long feedId, byte[] body, string? signature, CancellationToken cancellationToken)
    {
        var feed = await db.Feeds.SingleOrDefaultAsync(f => f.Id == feedId, cancellationToken);
        if (feed?.WebSubSecret is null || !SignatureMatches(feed.WebSubSecret, body, signature))
        {
            LogRejected(feedId);
            return false;
        }

        ParsedFeed parsed;
        try
        {
            parsed = FeedParser.Parse(body, new Uri(feed.SelfUrl ?? feed.Url));
        }
        catch (FeedParseException ex)
        {
            LogUnparseable(feedId, ex.Message);
            return false;
        }

        await ingester.IngestAsync(feed, parsed, new Uri(feed.Url), time.GetUtcNow().UtcDateTime, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal static bool SignatureMatches(string secret, byte[] body, string? header)
    {
        var parts = header?.Split('=', 2);
        if (parts is not [var method, var hex])
        {
            return false;
        }

        var key = Encoding.UTF8.GetBytes(secret);
        byte[]? expected = method.ToLowerInvariant() switch
        {
            "sha1" => HMACSHA1.HashData(key, body),
            "sha256" => HMACSHA256.HashData(key, body),
            "sha384" => HMACSHA384.HashData(key, body),
            "sha512" => HMACSHA512.HashData(key, body),
            _ => null,
        };
        if (expected is null)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(hex));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WebSub hub refused subscription for feed {FeedId}: HTTP {Status}")]
    private partial void LogHubRefused(long feedId, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebSub hub unreachable for feed {FeedId}: {Error}")]
    private partial void LogHubFailed(long feedId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WebSub delivery for feed {FeedId} ignored: unknown feed or bad signature")]
    private partial void LogRejected(long feedId);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebSub delivery for feed {FeedId} could not be parsed: {Error}")]
    private partial void LogUnparseable(long feedId, string error);
}
