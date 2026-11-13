namespace Skimr.Infrastructure.Content;

public sealed record ContentOptions
{
    public static readonly IReadOnlyList<string> DefaultEmbedHosts =
        ["www.youtube.com", "www.youtube-nocookie.com", "player.vimeo.com"];

    /// <summary>Hosts whose https iframes survive sanitising. Also feeds the CSP frame-src.</summary>
    public IReadOnlyList<string> AllowedEmbedHosts { get; init; } = DefaultEmbedHosts;

    /// <summary>
    /// Load article images through the app (on by default, agreed 2026-11-13): hides the reader's
    /// IP and referrer from image hosts, makes http:// images work, and keeps img-src to 'self'.
    /// </summary>
    public bool ImageProxyEnabled { get; init; } = true;
}
