using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Infrastructure.Persistence;
using Skimr.IntegrationTests.Infrastructure;

namespace Skimr.IntegrationTests;

public abstract partial class SmokeTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Liveness_returns_healthy()
    {
        using var client = app.Factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be("Healthy");
    }

    [Fact]
    public async Task Readiness_checks_database()
    {
        using var client = app.Factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void Migrations_match_the_model()
    {
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();

        db.Database.HasPendingModelChanges().Should().BeFalse("run 'dotnet ef migrations add' for this provider");
        db.Database.GetPendingMigrations().Should().BeEmpty();
    }

    [Fact]
    public async Task Pages_send_security_headers_and_have_no_inline_script_or_style()
    {
        using var client = app.Factory.CreateClient();

        var response = await client.GetAsync(new Uri("/setup", UriKind.Relative), Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Contain("script-src 'self';").And.Contain("frame-ancestors 'none'");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
        InlineScript().IsMatch(html).Should().BeFalse("the CSP blocks inline scripts");
        html.Should().NotContain("style=\"").And.NotContain("<style");
    }

    [Fact]
    public async Task Base_path_prefixes_routes_and_base_href()
    {
        await using var factory = app.WithSettings(("Server:BasePath", "/skimr"));
        using var client = factory.CreateClient();

        var health = await client.GetAsync(new Uri("/skimr/health", UriKind.Relative), Ct);
        var html = await client.GetStringAsync(new Uri("/skimr/setup", UriKind.Relative), Ct);

        health.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("<base href=\"/skimr/\"");
    }

    [GeneratedRegex(@"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();
}

public sealed class SqliteSmokeTests(SqliteAppFixture app) : SmokeTests(app), IClassFixture<SqliteAppFixture>;

public sealed class PostgresSmokeTests(PostgresAppFixture app) : SmokeTests(app), IClassFixture<PostgresAppFixture>;
