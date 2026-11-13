namespace Skimr.UnitTests;

/// <summary>
/// The CSP is <c>style-src 'self'; script-src 'self'</c>. An inline style or script written by an
/// interactive component would be silently blocked in the browser, and server-rendered HTML
/// checks can't see interactive renders, so check the source instead.
/// </summary>
public sealed class CspGuardTests
{
    private static string WebProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Skimr.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repo root not found."), "src", "Skimr.Web");
    }

    public static TheoryData<string> RazorFiles() =>
        new(Directory.EnumerateFiles(WebProject(), "*.razor", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(WebProject(), f)));

    [Theory]
    [MemberData(nameof(RazorFiles))]
    public void Components_have_no_inline_styles_or_scripts(string file)
    {
        var text = File.ReadAllText(Path.Combine(WebProject(), file));

        text.Should().NotContain("style=\"", "use a class in app.css");
        text.Should().NotMatchRegex(@"<script(?![^>]*\bsrc=)[^>]*>", "use a module file");
        text.Should().NotContain(" onclick=\"", "inline handlers are scripts too");
    }
}
