namespace Skimr.Infrastructure.Fetching;

public sealed record FetchOptions
{
    /// <summary>Floor for any refresh interval, per the spec.</summary>
    public const int MinIntervalMinutes = 5;

    /// <summary>Runs the background scheduler. Tests turn it off.</summary>
    public bool Enabled { get; init; } = true;

    public int Concurrency { get; init; } = 8;

    public int DefaultIntervalMinutes { get; init; } = 30;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public long MaxResponseBytes { get; init; } = 10 * 1024 * 1024;

    public int MaxRedirects { get; init; } = 5;

    public string UserAgent { get; init; } = "Skimr/1.0 (self-hosted feed reader)";

    /// <summary>Lets home-lab users fetch from LAN addresses. Cloud metadata IPs stay blocked (PLAN D24).</summary>
    public bool AllowPrivateNetworks { get; init; }

    /// <summary>Absolute base URL the app is reachable at, for WebSub callbacks. Null disables WebSub.</summary>
    public Uri? PublicUrl { get; init; }
}
