using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;

namespace Skimr.UnitTests.Fetching;

/// <summary>In-memory SQLite that lives as long as this object, shared by every context it creates.</summary>
internal sealed class TestDb : IDisposable
{
    private readonly string _connectionString = $"Data Source=skimr-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;

    public TestDb()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        using var db = Create();
        db.Database.EnsureCreated();
    }

    public SkimrDbContext Create() =>
        new(new DbContextOptionsBuilder<SkimrDbContext>().UseSqlite(_connectionString).Options);

    /// <summary>Subscriptions and article state need a real user row (foreign key).</summary>
    public Guid AddUser(Guid? id = null)
    {
        using var db = Create();
        var user = TestUsers.New(id);
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    public void Dispose() => _keepAlive.Dispose();
}

internal static class TestUsers
{
    public static SkimrUser New(Guid? id = null)
    {
        var name = "user-" + Guid.NewGuid().ToString("N")[..8];
        return new SkimrUser
        {
            Id = id ?? Guid.NewGuid(),
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
        };
    }
}

internal sealed class FakeTime(DateTime utcNow) : TimeProvider
{
    public DateTime UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
}

internal sealed class FakeOutboundHttp(Func<FetchRequest, FetchResponse> respond) : IOutboundHttp
{
    public List<FetchRequest> Requests { get; } = [];

    public List<(Uri Uri, IReadOnlyDictionary<string, string> Form)> Posts { get; } = [];

    public Func<FetchRequest, FetchResponse> Respond { get; set; } = respond;

    public Task<FetchResponse> GetAsync(FetchRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }

    public Task<FetchResponse> PostFormAsync(Uri uri, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
    {
        Posts.Add((uri, form));
        return Task.FromResult(Response(HttpStatusCode.Accepted, uri, []));
    }

    public static FetchResponse Response(
        HttpStatusCode status,
        Uri uri,
        byte[] body,
        string? etag = null,
        TimeSpan? maxAge = null,
        TimeSpan? retryAfter = null) =>
        new(status, uri, body, "application/rss+xml", etag, null, maxAge, retryAfter, null, null);

    public static FetchResponse Ok(Uri uri, string body, string? etag = null) =>
        Response(HttpStatusCode.OK, uri, Encoding.UTF8.GetBytes(body), etag);
}
