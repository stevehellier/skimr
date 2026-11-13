using System.Globalization;

namespace Skimr.Infrastructure.Reading;

public enum ViewKind
{
    All,
    Today,
    Saved,
    Folder,
    Source,
    Board,
    Search,
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

    public static ReaderView Board(long boardId) => new(ViewKind.Board, boardId);

    public static readonly ReaderView Search = new(ViewKind.Search);

    /// <summary>Parses a <see cref="Key"/> ("all", "today", "saved", "folder:12", "source:34", "board:5").</summary>
    public static bool TryParse(string? key, out ReaderView view)
    {
        view = All;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var parts = key.Trim().ToLowerInvariant().Split(':', 2);
        switch (parts)
        {
            case ["all"]:
                return true;
            case ["today"]:
                view = Today;
                return true;
            case ["saved"]:
                view = Saved;
                return true;
            case [var kind, var idText] when long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0:
                switch (kind)
                {
                    case "folder":
                        view = Folder(id);
                        return true;
                    case "source":
                        view = Source(id);
                        return true;
                    case "board":
                        view = Board(id);
                        return true;
                }

                break;
        }

        return false;
    }

    /// <summary>Stable key for remembered per-view settings: "all", "folder:12", "source:34".</summary>
    public string Key => Kind switch
    {
        ViewKind.Folder => "folder:" + Id.ToString(CultureInfo.InvariantCulture),
        ViewKind.Source => "source:" + Id.ToString(CultureInfo.InvariantCulture),
        ViewKind.Board => "board:" + Id.ToString(CultureInfo.InvariantCulture),
        _ => Kind.ToString().ToLowerInvariant(),
    };
}
