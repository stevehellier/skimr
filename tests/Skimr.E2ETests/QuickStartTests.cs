using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Skimr.E2ETests;

/// <summary>
/// The Phase 8 quick-start acceptance run against a real deployment (for example the compose
/// stack): setup wizard, OPML import, read an article. Skipped unless SKIMR_QUICKSTART_URL and
/// SKIMR_QUICKSTART_CODE are set; the OPML points at real public feeds, so it needs internet.
/// </summary>
public sealed class QuickStartTests
{
    private const string Opml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <opml version="2.0"><head><title>Quick start</title></head><body>
          <outline text="Tech">
            <outline type="rss" text="Rust Blog" xmlUrl="https://blog.rust-lang.org/feed.xml" />
            <outline type="rss" text=".NET Blog" xmlUrl="https://devblogs.microsoft.com/dotnet/feed/" />
          </outline>
        </body></opml>
        """;

    [Fact]
    public async Task Setup_import_and_read_against_a_running_server()
    {
        var url = Environment.GetEnvironmentVariable("SKIMR_QUICKSTART_URL");
        var code = Environment.GetEnvironmentVariable("SKIMR_QUICKSTART_CODE");
        Assert.SkipWhen(string.IsNullOrEmpty(url) || string.IsNullOrEmpty(code), "Set SKIMR_QUICKSTART_URL and SKIMR_QUICKSTART_CODE to run against a deployment.");
        var baseUrl = url!.TrimEnd('/') + "/";
        var timings = new List<string>();
        var clock = Stopwatch.StartNew();
        void Mark(string step) => timings.Add($"{step}: {clock.Elapsed.TotalSeconds:F1} s");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1400, Height = 900 } });
        page.SetDefaultTimeout(30_000);
        try
        {
            await RunAsync(page, baseUrl, code!, Mark);
        }
        catch
        {
            Mark("FAILED at " + page.Url);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "skimr-quickstart-failure.png") });
            throw;
        }
        finally
        {
            var output = Environment.GetEnvironmentVariable("SKIMR_QUICKSTART_RESULT");
            if (!string.IsNullOrEmpty(output))
            {
                await File.WriteAllLinesAsync(output, timings, CancellationToken.None);
            }
        }

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(2), string.Join("; ", timings));
    }

    private static async Task RunAsync(IPage page, string baseUrl, string code, Action<string> Mark)
    {

        // Setup wizard.
        await page.GotoAsync(baseUrl);
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/setup$"));
        await page.FillAsync("#code", code);
        await page.FillAsync("#username", "admin");
        await page.FillAsync("#password", "quick start password");
        await page.FillAsync("#confirm", "quick start password");
        await page.ClickAsync("button[type=submit]");
        await Assertions.Expect(page.Locator("nav.nav-panel")).ToBeVisibleAsync();
        Mark("Setup done");

        // OPML import.
        await page.GotoAsync(baseUrl + "organize");
        var file = Path.Combine(Path.GetTempPath(), $"quickstart-{Guid.NewGuid():N}.opml");
        await File.WriteAllTextAsync(file, Opml, TestContext.Current.CancellationToken);
        // InputFile's script attaches after the first interactive render; a file set before that
        // is missed (a person can't pick one that fast), so set it again if nothing happened.
        for (var attempt = 0; ; attempt++)
        {
            await page.SetInputFilesAsync("#opml-file", file);
            try
            {
                await Assertions.Expect(page.Locator(".alert.ok")).ToContainTextAsync("Imported 2", new() { Timeout = 5_000 });
                break;
            }
            catch (PlaywrightException) when (attempt < 2)
            {
            }
        }

        File.Delete(file);
        Mark("OPML imported");

        // Read: the first fetch runs in the background; the list checks back until articles arrive.
        await page.GotoAsync(baseUrl);
        await Assertions.Expect(page.Locator(".article-item").First).ToBeVisibleAsync(new() { Timeout = 90_000 });
        await page.Locator(".article-item button.open").First.ClickAsync();
        await Assertions.Expect(page.Locator(".article-pane .article-body")).ToBeVisibleAsync();
        Mark("First article open");
    }
}
