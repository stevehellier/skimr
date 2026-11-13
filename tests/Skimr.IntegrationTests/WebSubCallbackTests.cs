using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Domain;
using Skimr.Infrastructure.Persistence;
using Skimr.IntegrationTests.Infrastructure;

namespace Skimr.IntegrationTests;

public abstract class WebSubCallbackTests(AppFixture app)
{
    private const string Secret = "s3cret";
    private const string Topic = "https://push.example.com/feed";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> AddFeedAsync(string? secret = Secret)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();
        var feed = new Feed
        {
            Url = $"https://push.example.com/{Guid.NewGuid():N}",
            SelfUrl = Topic,
            WebSubSecret = secret,
            CreatedAt = DateTime.UtcNow,
            NextFetchAt = DateTime.UtcNow,
        };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync(Ct);
        db.Subscriptions.Add(new Subscription { UserId = Guid.NewGuid(), FeedId = feed.Id, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(Ct);
        return feed.Id;
    }

    private async Task<T> QueryAsync<T>(Func<SkimrDbContext, Task<T>> query)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<SkimrDbContext>());
    }

    [Fact]
    public async Task Verification_echoes_the_challenge_and_records_the_lease()
    {
        var id = await AddFeedAsync();
        using var client = app.Factory.CreateClient();

        var response = await client.GetAsync(new Uri($"/websub/{id}?hub.mode=subscribe&hub.topic={Uri.EscapeDataString(Topic)}&hub.challenge=abc123&hub.lease_seconds=3600", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be("abc123");
        var lease = await QueryAsync(db => db.Feeds.Where(f => f.Id == id).Select(f => f.WebSubLeaseExpiresAt).SingleAsync(Ct));
        lease.Should().BeCloseTo(DateTime.UtcNow.AddHours(1), TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData("https://other.example.com/feed")]
    [InlineData("")]
    public async Task Verification_for_the_wrong_topic_is_refused(string topic)
    {
        var id = await AddFeedAsync();
        using var client = app.Factory.CreateClient();

        var response = await client.GetAsync(new Uri($"/websub/{id}?hub.mode=subscribe&hub.topic={Uri.EscapeDataString(topic)}&hub.challenge=x", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Signed_delivery_is_ingested()
    {
        var id = await AddFeedAsync();
        var body = Encoding.UTF8.GetBytes("<rss version=\"2.0\"><channel><title>Push</title><item><title>Pushed</title><guid>push-1</guid></item></channel></rss>");

        var response = await PostAsync(id, body, "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body)));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await QueryAsync(db => db.Articles.Where(a => a.FeedId == id).Select(a => a.Title).ToListAsync(Ct))).Should().Equal("Pushed");
        (await QueryAsync(db => db.UserArticles.CountAsync(u => u.FeedId == id, Ct))).Should().Be(1);
    }

    [Theory]
    [InlineData("sha256=0000")]
    [InlineData(null)]
    public async Task Badly_signed_delivery_gets_2xx_but_is_ignored(string? signature)
    {
        var id = await AddFeedAsync();
        var body = Encoding.UTF8.GetBytes("<rss version=\"2.0\"><channel><title>Evil</title><item><title>Forged</title><guid>f</guid></item></channel></rss>");

        var response = await PostAsync(id, body, signature);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await QueryAsync(db => db.Articles.CountAsync(a => a.FeedId == id, Ct))).Should().Be(0);
    }

    [Fact]
    public async Task Delivery_to_a_feed_without_a_secret_is_ignored()
    {
        var id = await AddFeedAsync(secret: null);
        var body = Encoding.UTF8.GetBytes("<rss version=\"2.0\"><channel><item><title>X</title><guid>x</guid></item></channel></rss>");

        await PostAsync(id, body, "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Array.Empty<byte>(), body)));

        (await QueryAsync(db => db.Articles.CountAsync(a => a.FeedId == id, Ct))).Should().Be(0);
    }

    private async Task<HttpResponseMessage> PostAsync(long id, byte[] body, string? signature)
    {
        using var client = app.Factory.CreateClient();
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/rss+xml");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/websub/{id}", UriKind.Relative)) { Content = content };
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Hub-Signature", signature);
        }

        return await client.SendAsync(request, Ct);
    }
}

public sealed class SqliteWebSubCallbackTests(SqliteAppFixture app) : WebSubCallbackTests(app), IClassFixture<SqliteAppFixture>;

public sealed class PostgresWebSubCallbackTests(PostgresAppFixture app) : WebSubCallbackTests(app), IClassFixture<PostgresAppFixture>;
