// Seeds a SQLite database with 500 sources and 200,000 articles for one user, then times the
// reader's hot paths. Run on demand, not in CI:
//   dotnet run -c Release --project tools/Skimr.Perf -- [output.md]
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Skimr.Domain;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;
using Skimr.Infrastructure.Sqlite;

const int Feeds = 500;
const int ArticlesPerFeed = 400;
const int Folders = 20;
const int Warmup = 3;
const int Runs = 20;

// Default: no statistics, like a database straight after import. --optimize runs what the app runs at
// startup and after retention (SqliteMaintenance.Optimize), i.e. steady state.
var analyzeStats = args.Contains("--optimize");
var output = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? $"results/phase5-perf-{DateTime.UtcNow:yyyy-MM-dd}.md";
var dir = Path.Combine(Path.GetTempPath(), "skimr-perf", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var connectionString = $"Data Source={Path.Combine(dir, "perf.db")}";
var report = new StringBuilder();
void Log(string line)
{
    Console.WriteLine(line);
    report.AppendLine(line);
}

var services = new ServiceCollection()
    .AddDbContextFactory<SkimrDbContext>(o => o.UseSkimrSqlite(connectionString))
    .AddSingleton(TimeProvider.System)
    .AddSingleton<IArticleSearch>(new SqliteArticleSearch())
    .AddSingleton(new RetentionOptions { Days = 80 })
    .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>))
    .AddScoped<ReadingService>()
    .AddScoped<RetentionService>()
    .BuildServiceProvider();
var contexts = services.GetRequiredService<IDbContextFactory<SkimrDbContext>>();

// ---- Schema (the real migrations, so the FTS5 table and triggers exist) ----------------
await using (var db = await contexts.CreateDbContextAsync())
{
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await db.Database.MigrateAsync();
}

// ---- Seed with raw SQL in one transaction ------------------------------------------------
var userId = Guid.NewGuid();
var seedTimer = Stopwatch.StartNew();
var now = DateTime.UtcNow;
var random = new Random(42);
string[] words = ["rust", "kestrel", "database", "garden", "election", "football", "climate", "camera", "recipe", "travel"];
await using (var connection = new SqliteConnection(connectionString))
{
    await connection.OpenAsync();
    await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();

    async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    static string Ts(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);

    await ExecAsync(
        "INSERT INTO AspNetUsers (Id, UserName, NormalizedUserName, SecurityStamp, CreatedAt, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES ($id, 'perf', 'PERF', 's', $now, 0, 0, 0, 1, 0)",
        ("$id", userId.ToString().ToUpperInvariant()), ("$now", Ts(now)));
    for (var f = 1; f <= Folders; f++)
    {
        await ExecAsync("INSERT INTO Folders (Id, UserId, Name, SortOrder) VALUES ($id, $user, $name, $id)", ("$id", f), ("$user", userId.ToString().ToUpperInvariant()), ("$name", $"Folder {f}"));
    }

    await using var feedCmd = connection.CreateCommand();
    feedCmd.Transaction = tx;
    feedCmd.CommandText = "INSERT INTO Feeds (Id, Url, Title, CreatedAt, NextFetchAt, ConsecutiveFailures) VALUES ($id, $url, $title, $now, $next, 0); " +
        "INSERT INTO Subscriptions (Id, UserId, FeedId, CreatedAt, AlwaysFetchFullText, SortOrder) VALUES ($id, $user, $id, $now, 0, $id); " +
        "INSERT INTO SubscriptionFolders (SubscriptionId, FolderId, SortOrder) VALUES ($id, $folder, $id);";
    var p = new Dictionary<string, SqliteParameter>();
    foreach (var name in new[] { "$id", "$url", "$title", "$now", "$next", "$user", "$folder" })
    {
        p[name] = feedCmd.Parameters.Add(name, SqliteType.Text);
    }

    for (var f = 1; f <= Feeds; f++)
    {
        p["$id"].Value = f;
        p["$url"].Value = $"https://perf.example.com/{f}/feed";
        p["$title"].Value = $"Source {f}";
        p["$now"].Value = Ts(now);
        p["$next"].Value = Ts(now.AddDays(1));
        p["$user"].Value = userId.ToString().ToUpperInvariant();
        p["$folder"].Value = (f % Folders) + 1;
        await feedCmd.ExecuteNonQueryAsync();
    }

    await using var articleCmd = connection.CreateCommand();
    articleCmd.Transaction = tx;
    articleCmd.CommandText =
        "INSERT INTO Articles (Id, FeedId, DedupKey, Title, Snippet, SearchText, ContentHtml, ContentHash, PublishedAt, FetchedAt, LastSeenAt) VALUES ($id, $feed, $key, $title, $snippet, $text, $html, 'h', $pub, $pub, $seen); " +
        "INSERT INTO UserArticles (UserId, ArticleId, FeedId, PublishedAt, IsRead, IsSaved) VALUES ($user, $id, $feed, $pub, $read, $saved);";
    var a = new Dictionary<string, SqliteParameter>();
    foreach (var name in new[] { "$id", "$feed", "$key", "$title", "$snippet", "$text", "$html", "$pub", "$seen", "$user", "$read", "$saved" })
    {
        a[name] = articleCmd.Parameters.Add(name, SqliteType.Text);
    }

    var id = 0L;
    for (var f = 1; f <= Feeds; f++)
    {
        for (var i = 0; i < ArticlesPerFeed; i++)
        {
            id++;
            var published = now.AddMinutes(-random.Next(0, 120 * 24 * 60));
            var topic = words[random.Next(words.Length)];
            var body = $"This is the news about {topic} and the latest on {words[random.Next(words.Length)]}. " + string.Concat(Enumerable.Repeat("Ordinary words fill the rest of the article body so search has real text to index. ", 4));
            a["$id"].Value = id;
            a["$feed"].Value = f;
            a["$key"].Value = $"k{id}";
            a["$title"].Value = $"Article {id} about {topic}";
            a["$snippet"].Value = body[..200];
            a["$text"].Value = body;
            a["$html"].Value = $"<p>{body}</p>";
            a["$pub"].Value = Ts(published);
            a["$seen"].Value = Ts(published);
            a["$user"].Value = userId.ToString().ToUpperInvariant();
            a["$read"].Value = random.NextDouble() < 0.7 ? 1 : 0;
            a["$saved"].Value = random.NextDouble() < 0.01 ? 1 : 0;
            await articleCmd.ExecuteNonQueryAsync();
        }
    }

    await tx.CommitAsync();
}

// Five AI feeds with about 300 sparse matches each (one article in 667), as a month of filtering might leave.
const int AiFeeds = 5;
await using (var connection = new SqliteConnection(connectionString))
{
    await connection.OpenAsync();
    await using var aiCmd = connection.CreateCommand();
    aiCmd.CommandText =
        "INSERT INTO AiFeeds (Id, UserId, Name, Rule, CreatedAt, LastCheckedArticleId) VALUES ($id, $user, $name, 'rule', $now, 0); " +
        "INSERT INTO AiFeedMatches (AiFeedId, ArticleId, MatchedAt) SELECT $id, Id, $now FROM Articles WHERE Id % 667 = $id;";
    var aiId = aiCmd.Parameters.Add("$id", SqliteType.Integer);
    aiCmd.Parameters.AddWithValue("$user", userId.ToString().ToUpperInvariant());
    var aiName = aiCmd.Parameters.Add("$name", SqliteType.Text);
    aiCmd.Parameters.AddWithValue("$now", now.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
    for (var f = 1; f <= AiFeeds; f++)
    {
        aiId.Value = f;
        aiName.Value = $"AI feed {f}";
        await aiCmd.ExecuteNonQueryAsync();
    }
}

seedTimer.Stop();
if (analyzeStats)
    await using (var connection = new SqliteConnection(connectionString))
    {
        await connection.OpenAsync();
        await using var analyze = connection.CreateCommand();
        analyze.CommandText = SqliteMaintenance.Optimize;
        var optimizeTimer = Stopwatch.StartNew();
        await analyze.ExecuteNonQueryAsync();
        Console.WriteLine($"PRAGMA optimize took {optimizeTimer.Elapsed.TotalSeconds:F1} s");
        report.AppendLine($"Startup PRAGMA optimize on this database took {optimizeTimer.Elapsed.TotalSeconds:F1} s.");
    }

Log($"# Performance run ({DateTime.UtcNow:yyyy-MM-dd})");
Log("");
Log($"SQLite file in WAL mode, real migrations (FTS5 triggers on), {(analyzeStats ? "after the app's startup PRAGMA optimize (steady state)" : "no statistics yet (worst case: straight after a bulk load, before the next restart or retention run)")}. {Feeds} sources in {Folders} folders, {Feeds * ArticlesPerFeed:N0} articles for one user, 70% read, 1% saved, published over 120 days. Seeded in {seedTimer.Elapsed.TotalSeconds:F1} s.");
Log($"Machine: {Environment.ProcessorCount} logical CPUs, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}. Each operation: {Warmup} warm-up runs, then {Runs} timed runs.");
Log("");
Log("| Operation | Median | p95 | Target |");
Log("|---|---|---|---|");

var reading = services.GetRequiredService<ReadingService>();
var firstSource = 1L;
var ct = CancellationToken.None;

async Task TimeAsync(string name, Func<Task> action, string target = "")
{
    for (var i = 0; i < Warmup; i++)
    {
        await action();
    }

    var samples = new List<double>();
    for (var i = 0; i < Runs; i++)
    {
        var sw = Stopwatch.StartNew();
        await action();
        samples.Add(sw.Elapsed.TotalMilliseconds);
    }

    samples.Sort();
    var median = samples[samples.Count / 2];
    var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
    Log($"| {name} | {median:F1} ms | {p95:F1} ms | {target} |");
}

await TimeAsync("Nav counts (all sources, folders, Today, Read Later)", () => reading.GetNavAsync(userId, ct), "< 100 ms");
await TimeAsync("  Nav part: unread counts per feed", async () =>
{
    await using var db = await contexts.CreateDbContextAsync();
    await db.UserArticles.Where(u => u.UserId == userId && !u.IsRead).GroupBy(u => u.FeedId).Select(g => new { g.Key, C = g.Count() }).ToListAsync();
});
await TimeAsync("  Nav part: subscriptions with folders", async () =>
{
    await using var db = await contexts.CreateDbContextAsync();
    await db.Subscriptions.Where(s => s.UserId == userId).Select(s => new { s.Id, s.FeedId, T = s.CustomTitle ?? s.Feed!.Title, F = s.Folders.Select(f => f.FolderId).ToList() }).ToListAsync();
});
await TimeAsync("  Nav part: Today unread", async () =>
{
    await using var db = await contexts.CreateDbContextAsync();
    var since = DateTime.UtcNow.AddHours(-24);
    await db.UserArticles.Where(u => u.UserId == userId && u.PublishedAt >= since && !u.IsRead).Select(u => u.FeedId).ToListAsync();
});
await TimeAsync("  Nav part: saved count and boards", async () =>
{
    await using var db = await contexts.CreateDbContextAsync();
    await db.UserArticles.CountAsync(u => u.UserId == userId && u.IsSaved);
    await db.Boards.Where(b => b.UserId == userId).Select(b => new { b.Id, C = b.Articles.Count }).ToListAsync();
});
await TimeAsync("  Nav part: AI feed unread counts (5 feeds)", async () =>
{
    await using var db = await contexts.CreateDbContextAsync();
    // The same two queries ReadingService.GetNavAsync runs.
    var feeds = await db.AiFeeds.Where(f => f.UserId == userId).Select(f => f.Id).ToListAsync();
    var matches = await db.AiFeedMatches.Where(m => feeds.Contains(m.AiFeedId)).Select(m => new { m.AiFeedId, m.ArticleId }).ToListAsync();
    var ids = matches.Select(m => m.ArticleId).Distinct().ToList();
    await db.UserArticles.Where(u => u.UserId == userId && EF.Constant(ids).Contains(u.ArticleId) && !u.IsRead).Select(u => u.ArticleId).ToListAsync();
});
await TimeAsync("First page: All, unread only", () => reading.GetArticlesAsync(userId, ReaderView.All, ArticleSort.Newest, true, null, ct), "< 100 ms");
await TimeAsync("First page: All, everything", () => reading.GetArticlesAsync(userId, ReaderView.All, ArticleSort.Newest, false, null, ct), "< 100 ms");
await TimeAsync("First page: Today", () => reading.GetArticlesAsync(userId, ReaderView.Today, ArticleSort.Newest, true, null, ct), "< 100 ms");
await TimeAsync("First page: a folder (25 sources)", () => reading.GetArticlesAsync(userId, ReaderView.Folder(1), ArticleSort.Newest, true, null, ct), "< 100 ms");
await TimeAsync("First page: one source", () => reading.GetArticlesAsync(userId, ReaderView.Source(firstSource), ArticleSort.Newest, true, null, ct), "< 100 ms");
await TimeAsync("First page: oldest first", () => reading.GetArticlesAsync(userId, ReaderView.All, ArticleSort.Oldest, true, null, ct), "< 100 ms");
var page = await reading.GetArticlesAsync(userId, ReaderView.All, ArticleSort.Newest, false, null, ct);
await TimeAsync("First page: AI feed (sparse matches)", () => reading.GetArticlesAsync(userId, ReaderView.AiFeed(1), ArticleSort.Newest, true, null, ct), "< 100 ms");
await TimeAsync("First page: Read Later", () => reading.GetArticlesAsync(userId, ReaderView.Saved, ArticleSort.Newest, false, null, ct), "< 100 ms");
await TimeAsync("Next page (keyset)", () => reading.GetArticlesAsync(userId, ReaderView.All, ArticleSort.Newest, false, page.Next, ct), "< 100 ms");
await TimeAsync("Search: rare word (Article 12345)", () => reading.SearchAsync(userId, new SearchQuery("12345"), null, ct));
await TimeAsync("Search: common word (in every article)", () => reading.SearchAsync(userId, new SearchQuery("news"), null, ct));
await TimeAsync("Search: two words", () => reading.SearchAsync(userId, new SearchQuery("kestrel garden"), null, ct));
var nextUnread = (await reading.GetArticlesAsync(userId, ReaderView.All, ArticleSort.Newest, true, null, ct, 200)).Items.Select(i => i.ArticleId).ToQueue();
await TimeAsync("Per click: mark one read, then reload nav", async () =>
{
    await reading.SetReadAsync(userId, [nextUnread.Dequeue()], true, ct);
    await reading.GetNavAsync(userId, ct);
}, "< 150 ms");
await TimeAsync("Google Reader: unread ids, reading list (n=10000)", () =>
    reading.GetArticleRefsAsync(userId, ReaderView.All, new SyncFilter(Read: false), ArticleSort.Newest, null, 10_000, ct));
await TimeAsync("Google Reader: unread-count", () => reading.GetUnreadByFeedAsync(userId, ct));

// ---- Query plans -----------------------------------------------------------------------
await using (var db = await contexts.CreateDbContextAsync())
{
    await PlanAsync(db, "Query plan: unread counts per source", db.UserArticles
        .Where(u => u.UserId == userId && !u.IsRead)
        .GroupBy(u => u.FeedId)
        .Select(g => new { g.Key, Count = g.Count() })
        .ToQueryString());

    // The same shape ReadingService.GetArticleRefsAsync builds for the reading list with xt=read.
    await PlanAsync(db, "Query plan: Google Reader unread ids (reading list)", db.UserArticles
        .Where(u => u.UserId == userId && db.Subscriptions.Any(s => s.UserId == userId && s.FeedId == u.FeedId))
        .Where(u => !u.IsRead)
        .OrderByDescending(u => u.PublishedAt).ThenByDescending(u => u.ArticleId)
        .Take(10_001)
        .Select(u => new { u.ArticleId, u.PublishedAt })
        .ToQueryString());
}

async Task PlanAsync(SkimrDbContext db, string title, string sql)
{
    var plan = new List<string>();
    var connection = db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
        await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    // SQLite's ToQueryString starts with ".param set @name 'value'" lines (CLI syntax); turn them into parameters.
    var lines = sql.Split('\n');
    command.CommandText = "EXPLAIN QUERY PLAN " + string.Join('\n', lines.Where(l => !l.StartsWith(".param", StringComparison.Ordinal) && !l.StartsWith("--", StringComparison.Ordinal)));
    foreach (var line in lines.Where(l => l.StartsWith(".param set", StringComparison.Ordinal)))
    {
        var parts = line[".param set ".Length..].Split(' ', 2);
        var parameter = command.CreateParameter();
        parameter.ParameterName = parts[0];
        parameter.Value = parts[1].Trim().Trim('\'');
        command.Parameters.Add(parameter);
    }

    await using (var reader = await command.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }
    }

    Log("");
    Log("## " + title);
    Log("");
    Log("```");
    plan.ForEach(Log);
    Log("```");
}

// ---- Retention (destructive, so last) ---------------------------------------------------
var retentionTimer = Stopwatch.StartNew();
var purged = await services.GetRequiredService<RetentionService>().PurgeAsync(ct);
retentionTimer.Stop();
Log("");
Log($"## Retention (80 days)");
Log("");
Log($"Removed {purged.UserRows:N0} read rows and {purged.Articles:N0} articles in {retentionTimer.Elapsed.TotalSeconds:F1} s ({purged.UserRows / Math.Max(retentionTimer.Elapsed.TotalSeconds, 0.001):N0} rows/s), in batches of 1,000.");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
await File.WriteAllTextAsync(output, report.ToString());
Console.WriteLine($"Written to {output}");
SqliteConnection.ClearAllPools();
Directory.Delete(dir, recursive: true);

internal static class QueueExtensions
{
    public static Queue<T> ToQueue<T>(this IEnumerable<T> items) => new(items);
}
