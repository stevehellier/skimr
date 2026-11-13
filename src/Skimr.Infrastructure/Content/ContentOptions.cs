namespace Skimr.Infrastructure.Content;

public sealed record ContentOptions
{
    public static readonly IReadOnlyList<string> DefaultEmbedHosts =
        ["www.youtube.com", "www.youtube-nocookie.com", "player.vimeo.com"];

    /// <summary>Hosts whose https iframes survive sanitising. Also feeds the CSP frame-src.</summary>
    public IReadOnlyList<string> AllowedEmbedHosts { get; init; } = DefaultEmbedHosts;
}
