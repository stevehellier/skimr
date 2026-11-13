using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;
using Skimr.IntegrationTests.Infrastructure;

namespace Skimr.IntegrationTests;

/// <summary>Setup, sign-in, registration, tokens and admin rules. Each test gets an empty database.</summary>
public abstract partial class AuthTests(AppFixture fixture)
{
    private const string AdminPassword = "correct horse battery";
    private const string UserPassword = "another long password";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Helpers -----------------------------------------------------------------------

    private static Dictionary<string, string> SetupForm(string code, string userName, string mode = "InviteOnly") => new()
    {
        ["Input.Code"] = code,
        ["Input.UserName"] = userName,
        ["Input.Password"] = AdminPassword,
        ["Input.ConfirmPassword"] = AdminPassword,
        ["Input.Mode"] = mode,
    };

    private static string SetupCode(SkimrFactory app) => app.Services.GetRequiredService<SetupService>().Code!;

    /// <summary>Runs the wizard and returns a browser signed in as the admin.</summary>
    private static async Task<Browser> SetupAsync(SkimrFactory app, string mode = "InviteOnly")
    {
        var browser = Browser.Open(app);
        var response = await browser.SubmitAsync("/setup", "setup", SetupForm(SetupCode(app), "admin", mode));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync(Ct));
        return browser;
    }

    private static async Task<T> WithScopeAsync<T>(SkimrFactory app, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private static Task<Guid> CreateUserAsync(SkimrFactory app, string userName, bool isAdmin = false) =>
        WithScopeAsync(app, async sp => (await sp.GetRequiredService<AccountService>().CreateUserAsync(userName, UserPassword, isAdmin, Ct)).UserId!.Value);

    private static Task<int> UserCountAsync(SkimrFactory app) =>
        WithScopeAsync(app, sp => sp.GetRequiredService<SkimrDbContext>().Users.CountAsync(Ct));

    private static async Task<Browser> SignInAsync(SkimrFactory app, string userName, string password, string path = "/login")
    {
        var browser = Browser.Open(app);
        var response = await browser.SubmitAsync(path, "login", new Dictionary<string, string>
        {
            ["Input.UserName"] = userName,
            ["Input.Password"] = password,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return browser;
    }

    private static Task<HttpResponseMessage> RegisterAsync(Browser browser, string userName, string? invite = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["Input.UserName"] = userName,
            ["Input.Password"] = UserPassword,
            ["Input.ConfirmPassword"] = UserPassword,
        };
        if (invite is not null)
        {
            fields["Input.Invite"] = invite;
        }

        return browser.SubmitAsync(invite is null ? "/register" : $"/register?invite={Uri.EscapeDataString(invite)}", "register", fields);
    }

    // ---- First-run setup ---------------------------------------------------------------

    [Fact]
    public async Task Before_setup_pages_redirect_to_the_wizard_but_health_and_api_do_not()
    {
        await using var app = fixture.CreateFreshApp();
        using var browser = Browser.Open(app);

        (await browser.GetAsync("/")).Headers.Location!.OriginalString.Should().EndWith("/setup");
        (await browser.GetAsync("/login")).Headers.Location!.OriginalString.Should().EndWith("/setup");
        (await browser.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await browser.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Setup_rejects_a_wrong_code()
    {
        await using var app = fixture.CreateFreshApp();
        using var browser = Browser.Open(app);

        var html = await browser.SubmitForHtmlAsync("/setup", "setup", SetupForm("AAAA-BBBB-CCCC", "admin"));

        html.Should().Contain("setup code is wrong");
        (await UserCountAsync(app)).Should().Be(0);
    }

    [Fact]
    public async Task Setup_creates_a_signed_in_admin_and_then_closes()
    {
        await using var app = fixture.CreateFreshApp();
        var code = SetupCode(app);
        using var browser = await SetupAsync(app, mode: "Closed");

        (await browser.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await browser.GetAsync("/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK, "the first account is an admin");
        (await WithScopeAsync(app, sp => sp.GetRequiredService<AccountService>().GetRegistrationModeAsync(Ct))).Should().Be(RegistrationMode.Closed);

        using var second = Browser.Open(app);
        (await second.GetHtmlAsync("/setup")).Should().Contain("Setup is complete");
        (await second.GetAsync("/")).Headers.Location!.OriginalString.Should().Contain("/login", "after setup, pages need a sign-in");
        app.Services.GetRequiredService<SetupService>().Code.Should().BeNull("the code is single-use");
        _ = code;
    }

    [Fact]
    public async Task Concurrent_setup_submissions_create_exactly_one_admin()
    {
        await using var app = fixture.CreateFreshApp();
        var code = SetupCode(app);
        using var a = Browser.Open(app);
        using var b = Browser.Open(app);

        await Task.WhenAll(
            a.SubmitAsync("/setup", "setup", SetupForm(code, "first")),
            b.SubmitAsync("/setup", "setup", SetupForm(code, "second")));

        (await UserCountAsync(app)).Should().Be(1);
    }

    // ---- Sign-in -----------------------------------------------------------------------

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        await using var app = fixture.CreateFreshApp();
        using var _ = await SetupAsync(app);
        using var browser = Browser.Open(app);

        for (var i = 0; i < 5; i++)
        {
            var html = await browser.SubmitForHtmlAsync("/login", "login", new Dictionary<string, string> { ["Input.UserName"] = "admin", ["Input.Password"] = "wrong password!" });
            html.Should().Contain(i < 4 ? "Wrong user name or password" : "locked or disabled");
        }

        var locked = await browser.SubmitForHtmlAsync("/login", "login", new Dictionary<string, string> { ["Input.UserName"] = "admin", ["Input.Password"] = AdminPassword });
        locked.Should().Contain("locked or disabled", "the right password doesn't help during lockout");
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    public async Task Return_url_never_leaves_the_site(string returnUrl)
    {
        await using var app = fixture.CreateFreshApp();
        using var _ = await SetupAsync(app);
        using var browser = Browser.Open(app);

        var response = await browser.SubmitAsync($"/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}", "login",
            new Dictionary<string, string> { ["Input.UserName"] = "admin", ["Input.Password"] = AdminPassword });

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        (location.IsAbsoluteUri ? location.Host : "localhost").Should().Be("localhost");
        location.OriginalString.Should().NotContain("evil");
    }

    [Fact]
    public async Task Return_url_on_the_site_is_followed()
    {
        await using var app = fixture.CreateFreshApp();
        using var _ = await SetupAsync(app);
        using var browser = Browser.Open(app);

        var response = await browser.SubmitAsync("/login?ReturnUrl=%2Faccount", "login",
            new Dictionary<string, string> { ["Input.UserName"] = "admin", ["Input.Password"] = AdminPassword });

        response.Headers.Location!.OriginalString.Should().EndWith("/account");
    }

    // ---- Registration ------------------------------------------------------------------

    [Fact]
    public async Task Closed_registration_creates_no_account()
    {
        await using var app = fixture.CreateFreshApp();
        using var _ = await SetupAsync(app, mode: "Closed");
        using var browser = Browser.Open(app);

        (await browser.GetHtmlAsync("/register")).Should().Contain("Registration is closed");
        (await UserCountAsync(app)).Should().Be(1);
    }

    [Fact]
    public async Task Invite_only_needs_an_invite_and_an_invite_works_once()
    {
        await using var app = fixture.CreateFreshApp();
        using var admin = await SetupAsync(app);

        using var stranger = Browser.Open(app);
        (await stranger.GetHtmlAsync("/register")).Should().Contain("need an invite link");

        var inviteHtml = await admin.SubmitForHtmlAsync("/admin/users", "create-invite", new Dictionary<string, string> { ["NewInvite.Note"] = "for Sam" });
        var link = InviteLink().Match(inviteHtml).Groups[1].Value;
        var invite = Uri.UnescapeDataString(link[(link.IndexOf("invite=", StringComparison.Ordinal) + 7)..]);
        invite.Should().StartWith(AccountService.InvitePrefix);

        using var sam = Browser.Open(app);
        (await RegisterAsync(sam, "sam", invite)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await sam.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK, "sign-up signs you in");

        using var again = Browser.Open(app);
        (await (await RegisterAsync(again, "sam2", invite)).Content.ReadAsStringAsync(Ct)).Should().Contain("invalid, already used or expired");
        (await UserCountAsync(app)).Should().Be(2);
    }

    [Fact]
    public async Task One_invite_used_by_two_people_at_once_creates_one_account()
    {
        await using var app = fixture.CreateFreshApp();
        using var _ = await SetupAsync(app);
        var adminId = await WithScopeAsync(app, sp => sp.GetRequiredService<SkimrDbContext>().Users.Select(u => u.Id).SingleAsync(Ct));
        var invite = await WithScopeAsync(app, sp => sp.GetRequiredService<AccountService>().CreateInviteAsync(adminId, null, Ct));
        using var a = Browser.Open(app);
        using var b = Browser.Open(app);

        await Task.WhenAll(RegisterAsync(a, "racer-a", invite), RegisterAsync(b, "racer-b", invite));

        (await UserCountAsync(app)).Should().Be(2, "the admin plus exactly one of the two");
    }

    [Fact]
    public async Task Open_registration_needs_no_invite()
    {
        await using var app = fixture.CreateFreshApp();
        using var _ = await SetupAsync(app, mode: "Open");
        using var browser = Browser.Open(app);

        (await RegisterAsync(browser, "walk-in")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await UserCountAsync(app)).Should().Be(2);
    }

    // ---- API tokens --------------------------------------------------------------------

    [Fact]
    public async Task Token_from_the_account_page_works_on_the_api_until_revoked()
    {
        await using var app = fixture.CreateFreshApp();
        using var browser = await SetupAsync(app);

        var html = await browser.SubmitForHtmlAsync("/account", "create-token", new Dictionary<string, string> { ["NewToken.Name"] = "NetNewsWire" });
        var token = NewToken().Match(html).Groups[1].Value;
        token.Should().StartWith(ApiTokenService.Prefix);

        var me = await browser.ApiGetAsync("/api/v1/me", token);
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        (await me.Content.ReadFromJsonAsync<Dictionary<string, object>>(Ct))!["userName"].ToString().Should().Be("admin");

        // The stored hash must not be the token, and the plain token must not be anywhere in the table.
        var stored = await WithScopeAsync(app, sp => sp.GetRequiredService<SkimrDbContext>().ApiTokens.AsNoTracking().SingleAsync(Ct));
        stored.TokenHash.Should().NotBe(token).And.HaveLength(64);
        new[] { stored.Name, stored.Prefix, stored.TokenHash }.Should().NotContain(token);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, new Uri($"/api/v1/tokens/{stored.Id}", UriKind.Relative));
        delete.Headers.Authorization = new("Bearer", token);
        (await browser.Client.SendAsync(delete, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await browser.ApiGetAsync("/api/v1/me", token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_api_ignores_the_sign_in_cookie()
    {
        await using var app = fixture.CreateFreshApp();
        using var browser = await SetupAsync(app);

        (await browser.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tokens_stop_working_when_the_account_is_disabled_or_the_token_expires()
    {
        await using var app = fixture.CreateFreshApp();
        using var _ = await SetupAsync(app);
        var adminId = await WithScopeAsync(app, sp => sp.GetRequiredService<SkimrDbContext>().Users.Select(u => u.Id).SingleAsync(Ct));
        var userId = await CreateUserAsync(app, "pat");
        var userToken = await WithScopeAsync(app, async sp => (await sp.GetRequiredService<ApiTokenService>().CreateAsync(userId, "t", null, Ct)).PlainText);
        var adminToken = await WithScopeAsync(app, async sp => (await sp.GetRequiredService<ApiTokenService>().CreateAsync(adminId, "t", 30, Ct)).PlainText);
        using var client = Browser.Open(app);
        (await client.ApiGetAsync("/api/v1/me", userToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        await WithScopeAsync(app, sp => sp.GetRequiredService<AccountService>().SetDisabledAsync(adminId, userId, true, Ct));
        (await client.ApiGetAsync("/api/v1/me", userToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await WithScopeAsync(app, sp => sp.GetRequiredService<SkimrDbContext>().ApiTokens
            .Where(t => t.UserId == adminId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)), Ct));
        (await client.ApiGetAsync("/api/v1/me", adminToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tokens_can_be_created_through_the_api()
    {
        await using var app = fixture.CreateFreshApp();
        using var _ = await SetupAsync(app);
        var adminId = await WithScopeAsync(app, sp => sp.GetRequiredService<SkimrDbContext>().Users.Select(u => u.Id).SingleAsync(Ct));
        var token = await WithScopeAsync(app, async sp => (await sp.GetRequiredService<ApiTokenService>().CreateAsync(adminId, "t", null, Ct)).PlainText);
        using var client = Browser.Open(app);

        using var create = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/tokens", UriKind.Relative)) { Content = JsonContent.Create(new { name = "script", expiresInDays = 7 }) };
        create.Headers.Authorization = new("Bearer", token);
        var response = await client.Client.SendAsync(create, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(Ct);
        (await client.ApiGetAsync("/api/v1/me", body!["token"].ToString()!)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_temporary_lockout_does_not_break_the_users_tokens()
    {
        // Otherwise anyone who knows a user name could keep that user's sync apps offline.
        await using var app = fixture.CreateFreshApp();
        using var _ = await SetupAsync(app);
        var userId = await CreateUserAsync(app, "target");
        var token = await WithScopeAsync(app, async sp => (await sp.GetRequiredService<ApiTokenService>().CreateAsync(userId, "t", null, Ct)).PlainText);
        using var attacker = Browser.Open(app);
        for (var i = 0; i < 5; i++)
        {
            await attacker.SubmitAsync("/login", "login", new Dictionary<string, string> { ["Input.UserName"] = "target", ["Input.Password"] = "wrong password!" });
        }

        (await WithScopeAsync(app, sp => sp.GetRequiredService<AccountService>().ListUsersAsync(Ct))).Single(u => u.Id == userId).IsLockedOut.Should().BeTrue();
        (await attacker.ApiGetAsync("/api/v1/me", token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- Sign-out and assets -----------------------------------------------------------

    [Fact]
    public async Task Sign_out_needs_the_antiforgery_token_and_ends_the_session()
    {
        await using var app = fixture.CreateFreshApp();
        using var browser = await SetupAsync(app);

        using (var forged = new FormUrlEncodedContent(new Dictionary<string, string>()))
        {
            (await browser.Client.PostAsync(new Uri("/logout", UriKind.Relative), forged, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await browser.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK, "a forged sign-out does nothing");

        var html = await browser.GetHtmlAsync("/");
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        using var real = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
        var response = await browser.Client.PostAsync(new Uri("/logout", UriKind.Relative), real, Ct);

        response.Headers.Location!.OriginalString.Should().EndWith("/login");
        (await browser.GetAsync("/")).Headers.Location!.OriginalString.Should().Contain("/login");
    }

    [Fact]
    public async Task Signed_out_visitors_can_load_the_login_page_assets()
    {
        await using var app = fixture.CreateFreshApp();
        using var _ = await SetupAsync(app);
        using var visitor = Browser.Open(app);

        var html = await visitor.GetHtmlAsync("/login");
        var assets = AssetReference().Matches(html).Select(m => m.Groups[1].Value).Distinct().ToList();

        assets.Should().NotBeEmpty();
        foreach (var asset in assets)
        {
            (await visitor.GetAsync("/" + asset.TrimStart('/'))).StatusCode.Should().Be(HttpStatusCode.OK, asset);
        }
    }

    [Fact]
    public void Only_static_files_and_explicitly_anonymous_endpoints_skip_sign_in()
    {
        // Guards the fallback policy's exceptions. Whether the published image actually contains
        // the static files is checked by the Docker smoke test in CI.
        var endpoints = fixture.Factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .ToList();
        bool Anonymous(string route) => endpoints
            .Where(e => string.Equals(e.RoutePattern.RawText?.TrimStart('/'), route, StringComparison.OrdinalIgnoreCase))
            .Should().NotBeEmpty(route).And.Subject
            .All(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null);

        Anonymous("_framework/blazor.web.js").Should().BeTrue();
        Anonymous("login").Should().BeTrue();
        Anonymous("setup").Should().BeTrue();
        Anonymous("health/ready").Should().BeTrue();
        Anonymous("").Should().BeFalse("the home page needs a sign-in");
        Anonymous("account").Should().BeFalse();
        Anonymous("admin/users").Should().BeFalse();
    }

    // ---- Admin -------------------------------------------------------------------------

    [Fact]
    public async Task Non_admins_cannot_open_or_post_to_the_admin_page()
    {
        await using var app = fixture.CreateFreshApp();
        using var admin = await SetupAsync(app);
        var victimId = await CreateUserAsync(app, "victim");
        await CreateUserAsync(app, "mallory");
        using var mallory = await SignInAsync(app, "mallory", UserPassword);

        (await mallory.GetAsync("/admin/users")).Headers.Location!.OriginalString.Should().Contain("/access-denied");

        // Borrow a valid antiforgery token from a page Mallory can open, then post to the admin page.
        var html = await mallory.GetHtmlAsync("/account");
        var antiforgery = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgery,
            ["_handler"] = "user-action",
            ["Action.Command"] = $"disable:{victimId}",
        });
        var post = await mallory.Client.PostAsync(new Uri("/admin/users", UriKind.Relative), form, Ct);

        post.Headers.Location!.OriginalString.Should().Contain("/access-denied");
        (await WithScopeAsync(app, sp => sp.GetRequiredService<AccountService>().ListUsersAsync(Ct)))
            .Single(u => u.Id == victimId).IsDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task The_last_admin_cannot_be_removed_and_nobody_can_act_on_themselves()
    {
        await using var app = fixture.CreateFreshApp();
        using var _ = await SetupAsync(app);
        var adminId = await WithScopeAsync(app, sp => sp.GetRequiredService<SkimrDbContext>().Users.Select(u => u.Id).SingleAsync(Ct));
        var otherId = await CreateUserAsync(app, "other");

        await WithScopeAsync(app, async sp =>
        {
            var accounts = sp.GetRequiredService<AccountService>();
            (await accounts.SetDisabledAsync(adminId, adminId, true, Ct)).Succeeded.Should().BeFalse();
            (await accounts.DeleteAsync(adminId, adminId, Ct)).Succeeded.Should().BeFalse();
            (await accounts.SetAdminAsync(otherId, adminId, false, Ct)).Errors.Should().ContainSingle(e => e.Contains("last active admin"));
            (await accounts.DeleteAsync(otherId, adminId, Ct)).Errors.Should().ContainSingle(e => e.Contains("last active admin"));

            (await accounts.SetAdminAsync(adminId, otherId, true, Ct)).Succeeded.Should().BeTrue();
            (await accounts.SetAdminAsync(otherId, adminId, false, Ct)).Succeeded.Should().BeTrue("there's another admin now");
            return 0;
        });
    }

    [Fact]
    public async Task Disabling_a_user_ends_their_session()
    {
        // Validate the security stamp on every request instead of every minute.
        await using var app = fixture.CreateFreshApp();
        await using var fastStamps = app.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero)));
        using var admin = Browser.Open(fastStamps);
        (await admin.SubmitAsync("/setup", "setup", SetupForm(fastStamps.Services.GetRequiredService<SetupService>().Code!, "admin"))).StatusCode.Should().Be(HttpStatusCode.Redirect);

        Guid userId;
        await using (var scope = fastStamps.Services.CreateAsyncScope())
        {
            userId = (await scope.ServiceProvider.GetRequiredService<AccountService>().CreateUserAsync("leaver", UserPassword, false, Ct)).UserId!.Value;
        }

        using var leaver = Browser.Open(fastStamps);
        (await leaver.SubmitAsync("/login", "login", new Dictionary<string, string> { ["Input.UserName"] = "leaver", ["Input.Password"] = UserPassword }))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await leaver.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK);

        await admin.SubmitAsync("/admin/users", "user-action", new Dictionary<string, string> { ["Action.Command"] = $"disable:{userId}" });

        (await leaver.GetAsync("/")).Headers.Location!.OriginalString.Should().Contain("/login");
    }

    [GeneratedRegex("""data-testid="new-token">([^<]+)<""")]
    private static partial Regex NewToken();

    [GeneratedRegex("""data-testid="invite-link">([^<]+)<""")]
    private static partial Regex InviteLink();

    [GeneratedRegex("""(?:<link[^>]*href|<script[^>]*src)="([^"]+)""")]
    private static partial Regex AssetReference();
}

public sealed class SqliteAuthTests(SqliteAppFixture fixture) : AuthTests(fixture), IClassFixture<SqliteAppFixture>;

public sealed class PostgresAuthTests(PostgresAppFixture fixture) : AuthTests(fixture), IClassFixture<PostgresAppFixture>;

/// <summary>Uses a low limit, so it runs on its own app.</summary>
public sealed class RateLimitTests(SqliteAppFixture fixture) : IClassFixture<SqliteAppFixture>
{
    [Fact]
    public async Task Login_posts_are_rate_limited_but_viewing_the_page_is_not()
    {
        await using var app = fixture.CreateFreshApp(("Security:LoginAttemptsPerMinute", "3"));
        using var browser = Browser.Open(app);
        var code = app.Services.GetRequiredService<SetupService>().Code!;
        var form = new Dictionary<string, string> { ["Input.Code"] = "WRONG", ["Input.UserName"] = "x", ["Input.Password"] = "0123456789", ["Input.ConfirmPassword"] = "0123456789" };

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await browser.SubmitAsync("/setup", "setup", form)).StatusCode);
        }

        statuses.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
        (await browser.GetAsync("/setup")).StatusCode.Should().Be(HttpStatusCode.OK);
        _ = code;
    }

    [Fact]
    public async Task Api_rate_limit_is_per_user_not_per_address()
    {
        // In tests every request comes from the same (unknown) address, like users behind one proxy.
        await using var app = fixture.CreateFreshApp(("Security:ApiRequestsPerMinute", "10"));
        var ct = TestContext.Current.CancellationToken;
        string alice, bob;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
            var tokens = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
            var aliceId = (await accounts.CreateUserAsync("alice", "a long password", false, ct)).UserId!.Value;
            var bobId = (await accounts.CreateUserAsync("bob", "a long password", false, ct)).UserId!.Value;
            alice = (await tokens.CreateAsync(aliceId, "t", null, ct)).PlainText;
            bob = (await tokens.CreateAsync(bobId, "t", null, ct)).PlainText;
        }

        using var client = Browser.Open(app);
        for (var i = 0; i < 10; i++)
        {
            (await client.ApiGetAsync("/api/v1/me", alice)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await client.ApiGetAsync("/api/v1/me", alice)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await client.ApiGetAsync("/api/v1/me", bob)).StatusCode.Should().Be(HttpStatusCode.OK, "Bob has his own allowance");
    }
}
