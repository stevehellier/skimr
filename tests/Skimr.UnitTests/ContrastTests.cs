using System.Globalization;
using System.Text.RegularExpressions;

namespace Skimr.UnitTests;

/// <summary>
/// WCAG 2.2 AA contrast for every text/background pairing the stylesheet uses, in both themes,
/// computed from the colour tokens in app.css so a palette change can't silently regress.
/// </summary>
public sealed partial class ContrastTests
{
    private const double Text = 4.5;    // 1.4.3: body text
    private const double NonText = 3.0; // 1.4.11: input borders, focus ring

    /// <summary>(foreground, background, minimum ratio, what it's for)</summary>
    public static readonly TheoryData<string, string, double, string> Pairs = new()
    {
        { "text", "bg", Text, "body text" },
        { "text", "surface", Text, "nav text" },
        { "text", "surface-raised", Text, "dialog text" },
        { "text", "selected", Text, "current nav item, selected article" },
        { "unread", "bg", Text, "unread titles" },
        { "read", "bg", Text, "read titles" },
        { "read", "selected", Text, "read title when selected" },
        { "muted", "bg", Text, "dates, snippets, hints" },
        { "muted", "surface", Text, "nav counts" },
        { "muted", "selected", Text, "counts on the current nav item" },
        { "accent", "bg", Text, "links" },
        { "accent", "surface", Text, "links in the nav" },
        { "accent-text", "accent", Text, "primary buttons" },
        { "danger", "bg", Text, "danger buttons, errors" },
        { "danger", "surface", Text, "source error marker" },
        { "text", "ok-bg", Text, "success alerts" },
        { "text", "error-bg", Text, "error alerts" },
        { "control-border", "bg", NonText, "input and button outlines" },
        { "control-border", "surface", NonText, "input outlines in the nav" },
        { "focus", "bg", NonText, "focus ring" },
        { "focus", "surface", NonText, "focus ring in the nav" },
    };

    private static readonly Lazy<string> Css = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Skimr.slnx")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Skimr.Web", "wwwroot", "app.css"));
    });

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Light_theme_meets_AA(string foreground, string background, double minimum, string usage) =>
        Check(Tokens(LightBlock()), foreground, background, minimum, usage);

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Dark_theme_meets_AA(string foreground, string background, double minimum, string usage) =>
        Check(Tokens(DarkBlock()), foreground, background, minimum, usage);

    [Fact]
    public void Both_dark_blocks_are_identical()
    {
        // Dark values appear twice (system preference and explicit choice); they must not drift.
        var blocks = DarkBlocks().ToList();
        blocks.Should().HaveCount(2);
        Tokens(blocks[0]).Should().Equal(Tokens(blocks[1]));
    }

    private static void Check(Dictionary<string, string> tokens, string foreground, string background, double minimum, string usage)
    {
        var ratio = Ratio(tokens[foreground], tokens[background]);
        ratio.Should().BeGreaterThanOrEqualTo(minimum, $"--{foreground} on --{background} ({usage}) is {ratio:0.00}:1");
    }

    internal static double Ratio(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            var c = int.Parse(hex.AsSpan(1 + offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    private static string LightBlock() => RootBlock().Match(Css.Value).Groups[1].Value;

    private static string DarkBlock() => DarkBlocks().First();

    private static IEnumerable<string> DarkBlocks() => DarkRule().Matches(Css.Value).Select(m => m.Groups[1].Value);

    internal static Dictionary<string, string> Tokens(string block) =>
        Token().Matches(block).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToLowerInvariant());

    [GeneratedRegex(@"^:root \{([^}]*)\}", RegexOptions.Multiline)]
    private static partial Regex RootBlock();

    [GeneratedRegex(@":root(?::not\(\[data-theme=""light""\]\)|\[data-theme=""dark""\]) \{([^}]*)\}")]
    private static partial Regex DarkRule();

    [GeneratedRegex(@"--([a-z-]+):\s*(#[0-9a-fA-F]{6})")]
    private static partial Regex Token();
}
