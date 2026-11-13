namespace Skimr.Domain;

/// <summary>A cached favicon, stored under &lt;data&gt;/icons and served from the app.</summary>
public sealed class Icon
{
    public long Id { get; set; }

    /// <summary>SHA-256 hex of the bytes; identical icons are stored once.</summary>
    public required string Hash { get; set; }

    public required string MimeType { get; set; }

    public required string FileName { get; set; }

    public DateTime CreatedAt { get; set; }
}
