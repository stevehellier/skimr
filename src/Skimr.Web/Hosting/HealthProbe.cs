namespace Skimr.Web.Hosting;

/// <summary>
/// <c>Skimr --healthcheck [url]</c>: used by the Docker HEALTHCHECK, because the
/// chiseled image has no shell or curl. Exits 0 when /health/ready returns 2xx.
/// </summary>
internal static class HealthProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var url = args.Length > 1 ? args[1] : DefaultUrl();

        // Loopback probe of our own process, so it deliberately bypasses the SSRF-guarded client.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync(new Uri(url));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }

    private static string DefaultUrl()
    {
        var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        var port = ports?.Split([';', ','], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "8080";
        var basePath = (Environment.GetEnvironmentVariable("SKIMR__Server__BasePath") ?? "").TrimEnd('/');
        return $"http://localhost:{port}{basePath}/health/ready";
    }
}
