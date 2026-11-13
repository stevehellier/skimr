using System.Globalization;

namespace Skimr.Infrastructure.Reading;

public enum ViewKind
{
    All,
    Today,
    Saved,
    Folder,
    Source,
}

/// <summary>Which articles a list shows. <see cref="Id"/> is a folder id or a subscription id.</summary>
public readonly record struct ReaderView(ViewKind Kind, long Id = 0)
{
    public static readonly ReaderView All = new(ViewKind.All);
    public static readonly ReaderView Today = new(ViewKind.Today);
    public static readonly ReaderView Saved = new(ViewKind.Saved);

    /// <summary>"Today" means published in the last 24 hours (agreed 2026-11-13, PLAN D40).</summary>
    public static readonly TimeSpan TodayWindow = TimeSpan.FromHours(24);

    public static ReaderView Folder(long id) => new(ViewKind.Folder, id);

    public static ReaderView Source(long subscriptionId) => new(ViewKind.Source, subscriptionId);

    /// <summary>Stable key for remembered per-view settings: "all", "folder:12", "source:34".</summary>
    public string Key => Kind switch
    {
        ViewKind.Folder => "folder:" + Id.ToString(CultureInfo.InvariantCulture),
        ViewKind.Source => "source:" + Id.ToString(CultureInfo.InvariantCulture),
        _ => Kind.ToString().ToLowerInvariant(),
    };
}
