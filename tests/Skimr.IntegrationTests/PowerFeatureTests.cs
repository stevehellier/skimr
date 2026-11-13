using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Domain;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;
using Skimr.IntegrationTests.Infrastructure;

namespace Skimr.IntegrationTests;

/// <summary>Search, boards and retention on each provider (migrated databases, so the search index exists).</summary>
public abstract class PowerFeatureTests(AppFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => fixture.Factory.Services;

    private ReadingService Reading => Services.GetRequiredService<ReadingService>();

    private LibraryService Library => Services.GetRequiredService<LibraryService>();

    private BoardService Boards => Services.GetRequiredService<BoardService>();

    private async Task<T> DbAsync<T>(Func<SkimrDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SkimrDbContext>());
    }

    private Task<Guid> NewUserAsync() => DbAsync(db => TestData.AddUserAsync(db, Ct));

    /// <summary>A feed with the given (title, body) articles, one day apart, newest first.</summary>
    private Task<(string Url, List<long> Ids)> NewFeedAsync(params (string Title, string Body)[] items) => DbAsync(async db =>
    {
        var url = $"https://power-tests.example.com/{Guid.NewGuid():N}";
        var feed = new Feed { Url = url, Title = "Power", CreatedAt = DateTime.UtcNow, NextFetchAt = DateTime.UtcNow.AddDays(1) };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync(Ct);
        var articles = items.Select((item, i) => new Article
        {
            FeedId = feed.Id,
            DedupKey = Guid.NewGuid().ToString("N"),
            Title = item.Title,
            SearchText = item.Body,
            ContentHtml = $"<p>{item.Body}</p>",
            ContentHash = "h",
            PublishedAt = DateTime.UtcNow.AddDays(-i).AddMinutes(-1),
            FetchedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        }).ToList();
        db.Articles.AddRange(articles);
        await db.SaveChangesAsync(Ct);
        return (url, articles.Select(a => a.Id).ToList());
    });

    private async Task<long> SubscribeAsync(Guid user, string url, params long[] folders) =>
        (await Library.SubscribeAsync(user, url, folders, Ct)).Id!.Value;

    private async Task<List<string>> SearchTitlesAsync(Guid user, SearchQuery query) =>
        (await Reading.SearchAsync(user, query, null, Ct)).Items.Select(i => i.Title).ToList();

    // ---- Search ------------------------------------------------------------------------

    [Fact]
    public async Task Search_matches_title_and_body_text_but_not_html()
    {
        var token = "zq" + Guid.NewGuid().ToString("N")[..8];
        var (url, _) = await NewFeedAsync(($"Kestrel {token} release notes", "nothing here"), ("Plain title", $"the body mentions {token} once"), ("Unrelated", "paragraph text"));
        var alice = await NewUserAsync();
        await SubscribeAsync(alice, url);

        (await SearchTitlesAsync(alice, new SearchQuery(token))).Should().BeEquivalentTo([$"Kestrel {token} release notes", "Plain title"]);
        (await SearchTitlesAsync(alice, new SearchQuery($"{token} kestrel"))).Should().Equal($"Kestrel {token} release notes");
        (await SearchTitlesAsync(alice, new SearchQuery("paragraph"))).Should().Contain("Unrelated");
        (await SearchTitlesAsync(alice, new SearchQuery("<p>"))).Should().BeEmpty("markup isn't indexed");
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\"unclosed")]
    [InlineData("-")]
    [InlineData("NEAR(a b")]
    [InlineData("AND")]
    [InlineData("OR OR NOT")]
    [InlineData("title:x")]
    [InlineData("*")]
    [InlineData("(((")]
    [InlineData("a'b; DROP TABLE Articles; --")]
    [InlineData("\\")]
    [InlineData("^ ~ @ # $ % & | !")]
    [InlineData("    ")]
    [InlineData("café naïve 東京")]
    public async Task Hostile_or_odd_search_input_never_throws(string input)
    {
        var alice = await NewUserAsync();
        var (url, _) = await NewFeedAsync(("Something", "text"));
        await SubscribeAsync(alice, url);

        var act = () => Reading.SearchAsync(alice, new SearchQuery(input), null, Ct);

        await act.Should().NotThrowAsync();
        (await DbAsync(db => db.Articles.CountAsync(Ct))).Should().BeGreaterThan(0, "and nothing was dropped");
    }

    [Fact]
    public async Task Search_filters_by_source_folder_date_and_read_state()
    {
        var word = "fq" + Guid.NewGuid().ToString("N")[..8];
        var alice = await NewUserAsync();
        var folder = (await Library.CreateFolderAsync(alice, "Filtered", Ct)).Id!.Value;
        var (urlA, idsA) = await NewFeedAsync(($"{word} A0", ""), ($"{word} A1", ""), ($"{word} A2", ""));
        var (urlB, _) = await NewFeedAsync(($"{word} B0", ""));
        var sourceA = await SubscribeAsync(alice, urlA, folder);
        await SubscribeAsync(alice, urlB);
        await Reading.SetReadAsync(alice, [idsA[0]], true, Ct);

        (await SearchTitlesAsync(alice, new SearchQuery(word, SourceId: sourceA))).Should().BeEquivalentTo([$"{word} A0", $"{word} A1", $"{word} A2"]);
        (await SearchTitlesAsync(alice, new SearchQuery(word, FolderId: folder))).Should().HaveCount(3);
        (await SearchTitlesAsync(alice, new SearchQuery(word, From: DateTime.UtcNow.AddDays(-1.5)))).Should().BeEquivalentTo([$"{word} A0", $"{word} A1", $"{word} B0"]);
        (await SearchTitlesAsync(alice, new SearchQuery(word, To: DateTime.UtcNow.AddDays(-1.5)))).Should().Equal($"{word} A2");
        (await SearchTitlesAsync(alice, new SearchQuery(word, Read: ReadFilter.Read))).Should().Equal($"{word} A0");
        (await SearchTitlesAsync(alice, new SearchQuery(word, Read: ReadFilter.Unread))).Should().HaveCount(3);
        (await SearchTitlesAsync(alice, new SearchQuery($"{word[..5]}*"))).Should().HaveCount(4, "a trailing * searches by prefix");
    }

    [Fact]
    public async Task Search_only_finds_your_own_articles_and_ignores_others_filters()
    {
        var word = "iq" + Guid.NewGuid().ToString("N")[..8];
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();
        var folder = (await Library.CreateFolderAsync(alice, "Hers", Ct)).Id!.Value;
        var (url, _) = await NewFeedAsync(($"{word} secret", ""));
        var source = await SubscribeAsync(alice, url, folder);

        (await SearchTitlesAsync(bob, new SearchQuery(word))).Should().BeEmpty();
        (await SearchTitlesAsync(bob, new SearchQuery(word, SourceId: source))).Should().BeEmpty();
        (await SearchTitlesAsync(bob, new SearchQuery(word, FolderId: folder))).Should().BeEmpty();
        (await SearchTitlesAsync(alice, new SearchQuery(word))).Should().ContainSingle();
    }

    [Fact]
    public async Task The_index_follows_edits_and_deletes()
    {
        var before = "ea" + Guid.NewGuid().ToString("N")[..8];
        var after = "eb" + Guid.NewGuid().ToString("N")[..8];
        var alice = await NewUserAsync();
        var (url, ids) = await NewFeedAsync(($"{before} title", ""), ("Second", ""));
        await SubscribeAsync(alice, url);

        await DbAsync(db => db.Articles.Where(a => a.Id == ids[0]).ExecuteUpdateAsync(s => s.SetProperty(a => a.Title, $"{after} title"), Ct));
        (await SearchTitlesAsync(alice, new SearchQuery(before))).Should().BeEmpty();
        (await SearchTitlesAsync(alice, new SearchQuery(after))).Should().ContainSingle();

        await DbAsync(async db =>
        {
            await db.UserArticles.Where(u => u.ArticleId == ids[0]).ExecuteDeleteAsync(Ct);
            return await db.Articles.Where(a => a.Id == ids[0]).ExecuteDeleteAsync(Ct);
        });
        (await SearchTitlesAsync(alice, new SearchQuery(after))).Should().BeEmpty();
    }

    // ---- Boards ------------------------------------------------------------------------

    [Fact]
    public async Task Boards_collect_articles_and_show_in_nav_and_their_view()
    {
        var alice = await NewUserAsync();
        var (url, ids) = await NewFeedAsync(("One", ""), ("Two", ""), ("Three", ""));
        await SubscribeAsync(alice, url);
        var recipes = (await Boards.CreateAsync(alice, "Recipes", Ct)).Id!.Value;
        var later = (await Boards.CreateAsync(alice, "Later", Ct)).Id!.Value;
        (await Boards.CreateAsync(alice, "Recipes", Ct)).Succeeded.Should().BeFalse("names are unique per user");

        await Boards.AddAsync(alice, recipes, ids[0], Ct);
        await Boards.AddAsync(alice, recipes, ids[0], Ct);
        await Boards.AddAsync(alice, recipes, ids[2], Ct);
        await Boards.AddAsync(alice, later, ids[0], Ct);

        (await Reading.GetNavAsync(alice, Ct)).BoardList.Select(b => (b.Name, b.Count)).Should().Equal(("Recipes", 2), ("Later", 1));
        (await Reading.GetArticlesAsync(alice, ReaderView.Board(recipes), ArticleSort.Newest, true, null, Ct)).Items.Select(i => i.Title).Should().Equal("One", "Three");
        (await Boards.BoardsForArticleAsync(alice, ids[0], Ct)).Should().BeEquivalentTo([recipes, later]);

        await Boards.RemoveAsync(alice, recipes, ids[0], Ct);
        (await Reading.GetArticlesAsync(alice, ReaderView.Board(recipes), ArticleSort.Newest, true, null, Ct)).Items.Select(i => i.Title).Should().Equal("Three");
    }

    [Fact]
    public async Task Boards_are_private_and_only_hold_your_own_articles()
    {
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();
        var (url, ids) = await NewFeedAsync(("Hers", ""));
        await SubscribeAsync(alice, url);
        var hers = (await Boards.CreateAsync(alice, "Mine", Ct)).Id!.Value;
        var his = (await Boards.CreateAsync(bob, "Mine", Ct)).Id!.Value;
        await Boards.AddAsync(alice, hers, ids[0], Ct);

        (await Boards.AddAsync(bob, his, ids[0], Ct)).Succeeded.Should().BeFalse("Bob doesn't hold that article");
        (await Boards.AddAsync(bob, hers, ids[0], Ct)).Succeeded.Should().BeFalse("not his board");
        (await Boards.RenameAsync(bob, hers, "Stolen", Ct)).Succeeded.Should().BeFalse();
        (await Boards.DeleteAsync(bob, hers, Ct)).Succeeded.Should().BeFalse();
        await Boards.RemoveAsync(bob, hers, ids[0], Ct);
        (await Reading.GetArticlesAsync(bob, ReaderView.Board(hers), ArticleSort.Newest, false, null, Ct)).Items.Should().BeEmpty();
        (await Boards.BoardsForArticleAsync(bob, ids[0], Ct)).Should().BeEmpty();

        (await Reading.GetNavAsync(alice, Ct)).BoardList.Should().ContainSingle().Which.Count.Should().Be(1);
    }

    [Fact]
    public async Task Unsubscribing_keeps_boarded_articles()
    {
        var alice = await NewUserAsync();
        var (url, ids) = await NewFeedAsync(("Keep me", ""), ("Drop me", ""));
        var source = await SubscribeAsync(alice, url);
        var board = (await Boards.CreateAsync(alice, "Archive", Ct)).Id!.Value;
        await Boards.AddAsync(alice, board, ids[0], Ct);

        await Library.UnsubscribeAsync(alice, source, Ct);

        (await Reading.GetArticlesAsync(alice, ReaderView.Board(board), ArticleSort.Newest, false, null, Ct)).Items.Select(i => i.Title).Should().Equal("Keep me");
        (await Reading.GetNavAsync(alice, Ct)).AllUnread.Should().Be(0, "a boarded leftover doesn't count as unread");
    }

    // ---- Retention ---------------------------------------------------------------------

    [Fact]
    public async Task Retention_removes_old_read_articles_but_never_saved_boarded_unread_or_recent_ones()
    {
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();
        // Articles published 0, 100, 101, 102, 103 days ago (each a day apart from the second on).
        var (url, ids) = await NewFeedAsync(("recent read", ""), ("old read", ""), ("old unread", ""), ("old saved", ""), ("old boarded", ""));
        await DbAsync(db => db.Articles.Where(a => ids.Skip(1).Contains(a.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.PublishedAt, a => a.PublishedAt.AddDays(-100)).SetProperty(a => a.LastSeenAt, DateTime.UtcNow.AddDays(-100)), Ct));
        await SubscribeAsync(alice, url);
        await SubscribeAsync(bob, url);
        await Reading.SetReadAsync(alice, [ids[0], ids[1], ids[3], ids[4]], true, Ct);
        await Reading.SetReadAsync(bob, ids, true, Ct);
        await Reading.SetSavedAsync(alice, ids[3], true, Ct);
        var board = (await Boards.CreateAsync(alice, "Forever", Ct)).Id!.Value;
        await Boards.AddAsync(alice, board, ids[4], Ct);

        var result = await Services.GetRequiredService<RetentionService>().PurgeAsync(Ct);

        var aliceRows = await DbAsync(db => db.UserArticles.Where(u => u.UserId == alice).Select(u => u.ArticleId).ToListAsync(Ct));
        aliceRows.Should().BeEquivalentTo([ids[0], ids[2], ids[3], ids[4]], "only Alice's old read, unsaved, unboarded row went");
        var bobRows = await DbAsync(db => db.UserArticles.Where(u => u.UserId == bob).Select(u => u.ArticleId).ToListAsync(Ct));
        bobRows.Should().Equal(ids[0]);
        var remaining = await DbAsync(db => db.Articles.Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(Ct));
        remaining.Should().BeEquivalentTo([ids[0], ids[2], ids[3], ids[4]], "the old read article had no holders left and wasn't in the feed any more");
        result.UserRows.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task Retention_keeps_articles_still_in_the_feed_and_boarded_ones_even_without_holders()
    {
        var alice = await NewUserAsync();
        var (url, ids) = await NewFeedAsync(("still served", ""), ("boarded, source dropped", ""));
        await DbAsync(db => db.Articles.Where(a => ids.Contains(a.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.PublishedAt, DateTime.UtcNow.AddDays(-200)), Ct));
        await DbAsync(db => db.Articles.Where(a => a.Id == ids[1])
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, DateTime.UtcNow.AddDays(-200)), Ct));
        var source = await SubscribeAsync(alice, url);
        var board = (await Boards.CreateAsync(alice, "Kept", Ct)).Id!.Value;
        await Boards.AddAsync(alice, board, ids[1], Ct);
        await Reading.SetReadAsync(alice, ids, true, Ct);
        await Library.UnsubscribeAsync(alice, source, Ct);

        await Services.GetRequiredService<RetentionService>().PurgeAsync(Ct);

        (await DbAsync(db => db.Articles.Where(a => ids.Contains(a.Id)).Select(a => a.Title).ToListAsync(Ct)))
            .Should().BeEquivalentTo(["still served", "boarded, source dropped"]);
        (await Reading.GetArticlesAsync(alice, ReaderView.Board(board), ArticleSort.Newest, false, null, Ct)).Items.Should().ContainSingle();
    }
}

public sealed class SqlitePowerFeatureTests(SqliteAppFixture fixture) : PowerFeatureTests(fixture), IClassFixture<SqliteAppFixture>;

public sealed class PostgresPowerFeatureTests(PostgresAppFixture fixture) : PowerFeatureTests(fixture), IClassFixture<PostgresAppFixture>
{
    [Fact]
    public async Task Search_query_uses_the_gin_index()
    {
        // The query must repeat the index expression exactly, or Postgres silently scans every row.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();
        var generated = scope.ServiceProvider.GetRequiredService<IArticleSearch>().MatchingArticleIds(db, "kestrel").ToQueryString();
        // ToQueryString lists parameters as "-- @name=value" comments; inline them for EXPLAIN.
        var sql = string.Join('\n', generated.Split('\n').Where(l => !l.StartsWith("--", StringComparison.Ordinal)))
            .Replace("@query", "'kestrel'", StringComparison.Ordinal);

        await using var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        // Disable sequential scans so a small test table still shows whether the index is usable.
        command.CommandText = "SET enable_seqscan = off; EXPLAIN " + sql;
        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                plan.Add(reader.GetString(0));
            }
        }

        string.Join("\n", plan).Should().Contain("IX_Articles_Search", $"plan was:\n{string.Join("\n", plan)}\nfor:\n{sql}");
    }
}
