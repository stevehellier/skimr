using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Skimr.Infrastructure.Identity;
using Skimr.UnitTests.Fetching;

namespace Skimr.E2ETests;

/// <summary>
/// A real browser against the real app on Kestrel. Proves what server-side tests can't:
/// the Blazor circuit connects under the CSP with no violations, keyboard use works, focus
/// moves sensibly, the theme persists, and the layout works at phone width.
/// </summary>
public sealed partial class ReaderE2ETests : IAsyncLifetime
{
    private const string Password = "correct horse battery";

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "skimr-e2e", Guid.NewGuid().ToString("N"));
    private readonly List<string> _consoleErrors = [];
    private LocalHttpServer _feeds = null!;
    private KestrelFactory _app = null!;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private string _baseUrl = "";

    public static string ScreenshotDirectory => Path.Combine(AppContext.BaseDirectory, "screenshots");

    public async ValueTask InitializeAsync()
    {
        _feeds = await LocalHttpServer.StartAsync(app =>
        {
            app.MapGet("/site", (HttpContext ctx) => Results.Content(
                $"""<html><head><title>Local site</title><link rel="alternate" type="application/rss+xml" href="/feed.xml"></head><body>Hi</body></html>""",
                "text/html"));
            app.MapGet("/feed.xml", () => Results.Text(Rss(), "application/rss+xml"));
        });

        _app = new KestrelFactory(new Dictionary<string, string>
        {
            ["Database:Provider"] = "Sqlite",
            ["Storage:DataDirectory"] = _dataDirectory,
            ["Fetch:Enabled"] = "true",
            // The feed server is on loopback; a home-lab user would set this too.
            ["Fetch:AllowPrivateNetworkFeeds"] = "true",
            ["Security:LoginAttemptsPerMinute"] = "1000",
        });
        _app.UseKestrel(0);
        _app.StartServer();
        _baseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/') + "/";

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        Directory.CreateDirectory(ScreenshotDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
        await _app.DisposeAsync();
        await _feeds.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Temp space.
        }
    }

    private static string Rss() =>
        "<rss version=\"2.0\"><channel><title>Local Feed</title><link>http://127.0.0.1/</link>" +
        string.Concat(Enumerable.Range(1, 3).Select(i =>
            $"<item><title>Local story {i}</title><guid>s{i}</guid><link>https://example.com/{i}</link>" +
            $"<pubDate>{DateTime.UtcNow.AddHours(-i):R}</pubDate><description>&lt;p&gt;Body {i}&lt;/p&gt;</description></item>")) +
        "</channel></rss>";

    private async Task<IPage> NewPageAsync(BrowserNewContextOptions? options = null)
    {
        var context = await _browser.NewContextAsync(options ?? new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1400, Height = 900 } });
        var page = await context.NewPageAsync();
        // Every console error fails the test: CSP violations ("Refused to ..."), broken scripts, circuit errors.
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                _consoleErrors.Add(message.Text);
            }
        };
        page.PageError += (_, error) => _consoleErrors.Add(error);
        return page;
    }

    private static Task ShotAsync(IPage page, string name) =>
        page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(ScreenshotDirectory, name + ".png"), FullPage = false });

    private static Task<string?> ActiveElementIdAsync(IPage page) => page.EvaluateAsync<string?>("() => document.activeElement?.id ?? null");

    [Fact]
    public async Task Setup_subscribe_and_read_with_the_keyboard_on_desktop_and_phone()
    {
        // ---- Setup wizard ----------------------------------------------------------------
        var page = await NewPageAsync();
        await page.GotoAsync(_baseUrl);
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/setup$"));
        await page.FillAsync("#code", _app.Services.GetRequiredService<SetupService>().Code!);
        await page.FillAsync("#username", "admin");
        await page.FillAsync("#password", Password);
        await page.FillAsync("#confirm", Password);
        await page.ClickAsync("button[type=submit]");

        // The nav is rendered by the interactive component (no prerender), so seeing it proves the
        // circuit's WebSocket connected under the CSP.
        await Assertions.Expect(page.Locator("nav.nav-panel")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".empty")).ToContainTextAsync("No sources yet");

        // ---- Add a source by its site address (discovery) ---------------------------------
        await page.ClickAsync("text=+ Add source");
        await Assertions.Expect(page.Locator("#add-source-dialog")).ToBeVisibleAsync();
        await page.FillAsync("#source-url", _feeds.Url("site").AbsoluteUri);
        await page.ClickAsync("#add-source-dialog button[type=submit]");
        await Assertions.Expect(page.Locator("#add-source-dialog legend", new() { HasText = "Feeds found" })).ToBeVisibleAsync();
        await page.ClickAsync("#add-source-dialog >> text=Subscribe");
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/source/\\d+$"));

        // The first fetch runs in the background; the page checks back until articles arrive.
        try
        {
            await Assertions.Expect(page.Locator(".article-item")).ToHaveCountAsync(3, new() { Timeout = 20_000 });
        }
        catch (PlaywrightException ex)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Skimr.Infrastructure.Persistence.SkimrDbContext>();
            var health = string.Join("; ", db.Feeds.Select(f => $"{f.Url}: success={f.LastSuccessAt} error={f.LastError} next={f.NextFetchAt} articles={db.Articles.Count(a => a.FeedId == f.Id)} subs={db.Subscriptions.Count(s => s.FeedId == f.Id)} userArticles={db.UserArticles.Count(u => u.FeedId == f.Id)}")) + " url=" + page.Url;
            await page.ReloadAsync();
            await page.WaitForTimeoutAsync(3000);
            health += $" afterReload={await page.Locator(".article-item").CountAsync()} console=[{string.Join(" | ", _consoleErrors)}]";
            throw new InvalidOperationException($"Articles never appeared. Feeds: {health}", ex);
        }
        await Assertions.Expect(page.Locator("a.nav-link[href=''] .count")).ToHaveTextAsync("3");
        await ShotAsync(page, "desktop-list");

        // ---- Keyboard ----------------------------------------------------------------------
        await page.Locator("body").PressAsync("j");
        await Assertions.Expect(page.Locator(".article-pane h2")).ToHaveTextAsync("Local story 1");
        (await ActiveElementIdAsync(page)).Should().Be("pane-title", "opening an article moves focus to it");
        await Assertions.Expect(page.Locator("a.nav-link[href=''] .count")).ToHaveTextAsync("2", new() { Timeout = 5_000 });
        await ShotAsync(page, "desktop-reading");

        await page.Keyboard.PressAsync("j");
        await Assertions.Expect(page.Locator(".article-pane h2")).ToHaveTextAsync("Local story 2");
        await page.Keyboard.PressAsync("k");
        await Assertions.Expect(page.Locator(".article-pane h2")).ToHaveTextAsync("Local story 1");

        await page.Keyboard.PressAsync("m");
        await Assertions.Expect(page.Locator("li.article-item").First).ToHaveClassAsync(new Regex("\\bunread\\b"));
        await page.Keyboard.PressAsync("s");
        await Assertions.Expect(page.Locator("a.nav-link[href='saved'] .count")).ToHaveTextAsync("1");

        await page.Keyboard.PressAsync("?");
        await Assertions.Expect(page.Locator("#help-dialog")).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Locator("#help-dialog")).ToBeHiddenAsync();

        var openId = await page.Locator("li.article-item.selected").GetAttributeAsync("data-article-id");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Locator(".article-pane")).ToHaveCountAsync(0);
        (await ActiveElementIdAsync(page)).Should().Be($"article-{openId}", "closing returns focus to the article in the list");

        await page.Keyboard.PressAsync("g");
        await page.Keyboard.PressAsync("s");
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/saved$"));
        await Assertions.Expect(page.Locator(".article-item")).ToHaveCountAsync(1);

        // ---- Mark all read ---------------------------------------------------------------
        await page.GotoAsync(_baseUrl);
        // Unread only: story 2 was read on the way past with j/k.
        await Assertions.Expect(page.Locator(".article-item")).ToHaveCountAsync(2);
        await page.Locator("body").PressAsync("Shift+A");
        await Assertions.Expect(page.Locator("#mark-all-dialog")).ToBeVisibleAsync();
        await page.ClickAsync("#mark-all-dialog >> text=Everything");
        await Assertions.Expect(page.Locator(".empty")).ToContainTextAsync("All caught up");

        // ---- Theme persists across reloads ------------------------------------------------
        await page.GotoAsync(_baseUrl + "settings");
        await page.CheckAsync("text=Dark");
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        await page.GotoAsync(_baseUrl + "saved");
        await Assertions.Expect(page.Locator(".article-item")).ToHaveCountAsync(1);
        await ShotAsync(page, "desktop-dark");

        // ---- Phone width -------------------------------------------------------------------
        var state = await page.Context.StorageStateAsync();
        var phone = await NewPageAsync(new BrowserNewContextOptions
        {
            StorageState = state,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            IsMobile = true,
            HasTouch = true,
        });
        await phone.GotoAsync(_baseUrl + "saved");
        await Assertions.Expect(phone.Locator(".article-item")).ToHaveCountAsync(1);
        await Assertions.Expect(phone.Locator(".reader-nav")).ToBeHiddenAsync();
        await ShotAsync(phone, "phone-list");

        await phone.ClickAsync(".nav-toggle");
        await Assertions.Expect(phone.Locator(".reader-nav")).ToBeVisibleAsync();
        await ShotAsync(phone, "phone-nav");
        await phone.ClickAsync(".nav-close");
        await Assertions.Expect(phone.Locator(".reader-nav")).ToBeHiddenAsync();

        await phone.ClickAsync("button.open");
        await Assertions.Expect(phone.Locator(".article-pane")).ToBeVisibleAsync();
        var pane = await phone.Locator(".article-pane").BoundingBoxAsync();
        pane!.Width.Should().BeGreaterThanOrEqualTo(380, "the article takes the whole phone screen");
        (await phone.EvaluateAsync<int>("() => document.documentElement.scrollWidth")).Should().BeLessThanOrEqualTo(390, "nothing overflows sideways");
        await ShotAsync(phone, "phone-reading");

        // ---- No console errors anywhere ---------------------------------------------------
        _consoleErrors.Should().BeEmpty("CSP violations and script errors show up as console errors");
    }

    private sealed class KestrelFactory(IReadOnlyDictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseStaticWebAssets();
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }
    }
}
