using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Skimr.Infrastructure.Identity;
using Skimr.UnitTests.Fetching;
using Skimr.Web.Auth;

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
            app.MapGet("/feed.xml", () => Results.Text(Rss(imageUrl: _feeds.Url("pic.png").AbsoluteUri), "application/rss+xml"));
            app.MapGet("/pic.png", () => Results.Bytes(Convert.FromBase64String(OnePixelPng), "image/png"));
            app.MapGet("/big.xml", () => Results.Text(Rss(60, "Big"), "application/rss+xml"));
        });

        _app = new KestrelFactory(new Dictionary<string, string>
        {
            ["Database:Provider"] = "Sqlite",
            ["Storage:DataDirectory"] = _dataDirectory,
            ["Fetch:Enabled"] = "true",
            ["Retention:Enabled"] = "false",
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

    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static string Rss(int count = 3, string name = "Local", string? imageUrl = null) =>
        $"<rss version=\"2.0\"><channel><title>{name} Feed</title><link>http://127.0.0.1/</link>" +
        string.Concat(Enumerable.Range(1, count).Select(i =>
            $"<item><title>{name} story {i}</title><guid>{name}{i}</guid><link>https://example.com/{i}</link>" +
            $"<pubDate>{DateTime.UtcNow.AddHours(-i):R}</pubDate><description>&lt;p&gt;Body {i}&lt;/p&gt;{(i == 1 && imageUrl is not null ? $"&lt;img src=\"{imageUrl}\" alt=\"pixel\"&gt;" : "")}</description></item>")) +
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
        await Assertions.Expect(page.Locator("a.nav-link[href=''] .count .n")).ToHaveTextAsync("3");
        await ShotAsync(page, "desktop-list");

        // ---- Keyboard ----------------------------------------------------------------------
        await page.Locator("body").PressAsync("j");
        await Assertions.Expect(page.Locator(".article-pane h2")).ToHaveTextAsync("Local story 1");
        (await ActiveElementIdAsync(page)).Should().Be("pane-title", "opening an article moves focus to it");

        // The feed's http:// image loads through the proxy (same origin, so the CSP allows it).
        var pixel = page.Locator(".article-body img");
        (await pixel.GetAttributeAsync("src")).Should().StartWith("img/");
        await Assertions.Expect(pixel).ToHaveJSPropertyAsync("naturalWidth", 1);
        await Assertions.Expect(page.Locator("a.nav-link[href=''] .count .n")).ToHaveTextAsync("2", new() { Timeout = 5_000 });
        await ShotAsync(page, "desktop-reading");

        await page.Keyboard.PressAsync("j");
        await Assertions.Expect(page.Locator(".article-pane h2")).ToHaveTextAsync("Local story 2");
        await page.Keyboard.PressAsync("k");
        await Assertions.Expect(page.Locator(".article-pane h2")).ToHaveTextAsync("Local story 1");

        await page.Keyboard.PressAsync("m");
        await Assertions.Expect(page.Locator("li.article-item").First).ToHaveClassAsync(new Regex("\\bunread\\b"));
        await page.Keyboard.PressAsync("s");
        await Assertions.Expect(page.Locator("a.nav-link[href='saved'] .count .n")).ToHaveTextAsync("1");

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

        // ---- Search ------------------------------------------------------------------------
        await page.Locator("body").PressAsync("/");
        (await ActiveElementIdAsync(page)).Should().Be("search-box", "/ focuses search");
        await page.Keyboard.TypeAsync("story 2");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/search\\?q=story%202$"));
        await Assertions.Expect(page.Locator(".article-item .title")).ToHaveTextAsync(["Local story 2"]);

        await page.FillAsync("#search-box", "story 3");
        await page.PressAsync("#search-box", "Enter");
        await Assertions.Expect(page.Locator(".article-item .title")).ToHaveTextAsync(["Local story 3"]);

        await page.FillAsync("#search-box", "\"NEAR( -AND");
        await page.PressAsync("#search-box", "Enter");
        await Assertions.Expect(page.Locator(".empty")).ToContainTextAsync("No articles match");

        // ---- Boards ------------------------------------------------------------------------
        await page.FillAsync("#search-box", "story 3");
        await page.PressAsync("#search-box", "Enter");
        await page.ClickAsync("button.open");
        await page.ClickAsync(".board-picker summary");
        await page.FillAsync("#new-board", "Keepers");
        await page.ClickAsync(".board-picker button[type=submit]");
        await Assertions.Expect(page.Locator(".board-picker input[type=checkbox]")).ToBeCheckedAsync();
        await Assertions.Expect(page.Locator("a.nav-link", new() { HasText = "Keepers" })).ToBeVisibleAsync();
        await page.ClickAsync("a.nav-link >> text=Keepers");
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/board/\\d+$"));
        await Assertions.Expect(page.Locator(".article-item .title")).ToHaveTextAsync(["Local story 3"]);
        await ShotAsync(page, "desktop-board");

        // ---- Mark all read ---------------------------------------------------------------
        await page.GotoAsync(_baseUrl);
        // Unread only: story 2 was read on the way past with j/k, story 3 when opened from search.
        await Assertions.Expect(page.Locator(".article-item")).ToHaveCountAsync(1);
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

        // ---- PWA ---------------------------------------------------------------------------
        var swScope = await page.EvaluateAsync<string>("() => navigator.serviceWorker.ready.then(r => r.scope)");
        swScope.Should().Be(_baseUrl);
        var manifest = await page.APIRequest.GetAsync(_baseUrl + "manifest.webmanifest");
        manifest.Status.Should().Be(200);
        manifest.Headers["content-type"].Should().Contain("manifest+json");
        (await manifest.TextAsync()).Should().Contain("\"start_url\": \"./\"");
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator(".article-item")).ToHaveCountAsync(1);
        var cached = await page.EvaluateAsync<string[]>("() => caches.keys().then(ks => Promise.all(ks.map(k => caches.open(k).then(c => c.keys())))).then(all => all.flat().map(r => r.url))");
        cached.Should().Contain(u => u.EndsWith("offline.html", StringComparison.Ordinal));
        cached.Where(u => !u.EndsWith("offline.html", StringComparison.Ordinal)).Should().OnlyContain(
            u => Regex.IsMatch(u, @"\.[a-z0-9]{8,}\.(css|js|png|svg|woff2?)$", RegexOptions.IgnoreCase),
            "only fingerprinted static files are cached: never pages, API, proxied images or icons");

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

            // Revalidate sessions every second instead of every minute, so the disable test runs quickly.
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(new CircuitSessionOptions(TimeSpan.FromSeconds(1)));
                services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
            });
        }
    }

    // ---- Helpers for the shorter tests -----------------------------------------------------

    private async Task<Guid> CreateUserAsync(string userName, bool isAdmin)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        if (_app.Services.GetRequiredService<SetupService>().Code is { } code)
        {
            (await _app.Services.GetRequiredService<SetupService>().CompleteAsync(code, "admin", Password, RegistrationMode.Closed, CancellationToken.None)).Succeeded.Should().BeTrue();
        }

        var result = await scope.ServiceProvider.GetRequiredService<AccountService>().CreateUserAsync(userName, Password, isAdmin, CancellationToken.None);
        result.Succeeded.Should().BeTrue(string.Join(", ", result.Errors));
        return result.UserId!.Value;
    }

    private async Task<IPage> SignInAsync(string userName, BrowserNewContextOptions? options = null)
    {
        var page = await NewPageAsync(options);
        await page.GotoAsync(_baseUrl + "login");
        await page.FillAsync("#username", userName);
        await page.FillAsync("#password", Password);
        await page.ClickAsync("button[type=submit]");
        await Assertions.Expect(page.Locator("nav.nav-panel")).ToBeVisibleAsync();
        return page;
    }

    private async Task SubscribeAsync(Guid userId, string path)
    {
        var library = _app.Services.GetRequiredService<Skimr.Infrastructure.Reading.LibraryService>();
        (await library.SubscribeAsync(userId, _feeds.Url(path).AbsoluteUri, [], CancellationToken.None)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Scrolling_marks_articles_read_and_loads_more()
    {
        var userId = await CreateUserAsync("scroller", isAdmin: false);
        await SubscribeAsync(userId, "big.xml");
        var page = await SignInAsync("scroller");
        await Assertions.Expect(page.Locator("a.nav-link[href=''] .count .n")).ToHaveTextAsync("60", new() { Timeout = 20_000 });

        await page.GotoAsync(_baseUrl + "settings");
        await page.CheckAsync("text=When I open it or scroll past it in the list");
        await Assertions.Expect(page.Locator(".alert.ok")).ToBeVisibleAsync();

        await page.GotoAsync(_baseUrl);
        await Assertions.Expect(page.Locator(".article-item")).ToHaveCountAsync(Skimr.Infrastructure.Reading.ReadingService.PageSize);

        // Scroll to the bottom a few times: the end sentinel loads the next page, and everything
        // scrolled up past the top of the viewport is marked read.
        for (var i = 0; i < 40 && await page.Locator(".article-item").CountAsync() <= Skimr.Infrastructure.Reading.ReadingService.PageSize; i++)
        {
            await page.EvaluateAsync("() => window.scrollTo(0, document.body.scrollHeight)");
            await page.WaitForTimeoutAsync(250);
        }

        await Assertions.Expect(page.Locator("a.nav-link[href=''] .count .n")).Not.ToHaveTextAsync("60", new() { Timeout = 10_000 });

        (await page.Locator(".article-item").CountAsync()).Should().BeGreaterThan(Skimr.Infrastructure.Reading.ReadingService.PageSize, "infinite scroll loaded the next page");
        await Assertions.Expect(page.Locator("li.article-item.read").First).ToBeVisibleAsync();
        var remaining = int.Parse(await page.Locator("a.nav-link[href=''] .count .n").TextContentAsync() ?? "60", System.Globalization.CultureInfo.InvariantCulture);
        remaining.Should().BeLessThan(60, "scrolling past articles marked them read");
        await ShotAsync(page, "scroll-marked-read");
        _consoleErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task A_disabled_users_open_tab_stops_working()
    {
        var adminId = await CreateUserAsync("boss", isAdmin: true);
        var userId = await CreateUserAsync("leaver", isAdmin: false);
        await SubscribeAsync(userId, "feed.xml");
        // Service workers off: Playwright can't intercept requests a service worker makes, and this
        // test needs to see (and block) the redirect to sign-in.
        var page = await SignInAsync("leaver", new BrowserNewContextOptions { ServiceWorkers = ServiceWorkerPolicy.Block });
        await Assertions.Expect(page.Locator(".article-item")).ToHaveCountAsync(3, new() { Timeout = 20_000 });

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<AccountService>().SetDisabledAsync(adminId, userId, true, CancellationToken.None)).Succeeded.Should().BeTrue();
        }

        // The open tab is sent to sign in (the redirect is aborted here so the page stays put),
        // and the page can't act as the user any more. SignedOutCircuitGuardTests proves the
        // server drops a modified client's events even if it ignores the redirect.
        // The guard closes, then asks the browser to go to /login; wait for that attempt rather than a fixed time.
        var redirected = new TaskCompletionSource();
        await page.RouteAsync("**/login**", async route =>
        {
            redirected.TrySetResult();
            await route.AbortAsync();
        });
        await redirected.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await page.Locator("body").PressAsync("j");
        await page.WaitForTimeoutAsync(1_000);
        await Assertions.Expect(page.Locator(".article-pane")).ToHaveCountAsync(0);

        var stillReadable = await _app.Services.GetRequiredService<Skimr.Infrastructure.Reading.ReadingService>()
            .GetNavAsync(userId, CancellationToken.None);
        stillReadable.AllUnread.Should().Be(3, "nothing was marked read after the account was disabled");
    }
}
