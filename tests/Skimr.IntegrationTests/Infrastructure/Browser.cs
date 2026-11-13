using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Skimr.IntegrationTests.Infrastructure;

/// <summary>
/// Drives static server-rendered Blazor forms like a browser: keeps cookies, doesn't follow
/// redirects (so tests can assert them), and posts with the antiforgery token and the
/// <c>_handler</c> form name Blazor uses to route a submission.
/// </summary>
internal sealed partial class Browser(HttpClient client) : IDisposable
{
    public HttpClient Client { get; } = client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Browser Open(WebApplicationFactory<Program> app) =>
        new(app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));

    public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(new Uri(path, UriKind.Relative), Ct);

    public async Task<string> GetHtmlAsync(string path) => await (await GetAsync(path)).Content.ReadAsStringAsync(Ct);

    /// <summary>GETs <paramref name="path"/> for a fresh antiforgery token, then submits the named form.</summary>
    public async Task<HttpResponseMessage> SubmitAsync(string path, string formName, IReadOnlyDictionary<string, string> fields)
    {
        var html = await GetHtmlAsync(path);
        var token = AntiforgeryToken().Match(html);
        if (!token.Success)
        {
            throw new InvalidOperationException($"No antiforgery token on {path}. Page was:\n{html}");
        }

        var form = new Dictionary<string, string>(fields)
        {
            ["__RequestVerificationToken"] = token.Groups[1].Value,
            ["_handler"] = formName,
        };
        using var content = new FormUrlEncodedContent(form);
        return await Client.PostAsync(new Uri(path, UriKind.Relative), content, Ct);
    }

    public async Task<string> SubmitForHtmlAsync(string path, string formName, IReadOnlyDictionary<string, string> fields) =>
        await (await SubmitAsync(path, formName, fields)).Content.ReadAsStringAsync(Ct);

    public Task<HttpResponseMessage> ApiGetAsync(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Client.SendAsync(request, Ct);
    }

    public void Dispose() => Client.Dispose();

    [GeneratedRegex(@"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex AntiforgeryToken();
}
