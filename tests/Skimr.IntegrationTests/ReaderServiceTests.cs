using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Domain;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;
using Skimr.IntegrationTests.Infrastructure;

namespace Skimr.IntegrationTests;

/// <summary>Reading and library services on each provider: multi-user isolation, counts, paging.</summary>
public abstract class ReaderServiceTests(AppFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => fixture.Factory.Services;

    private ReadingService Reading => Services.GetRequiredService<ReadingService>();

    private LibraryService Library => Services.GetRequiredService<LibraryService>();

    private async Task<T> DbAsync<T>(Func<SkimrDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SkimrDbContext>());
    }

    private Task<Guid> NewUserAsync() => DbAsync(db => TestData.AddUserAsync(db, Ct));

    /// <summary>A feed that already has <paramref name="count"/> articles, one hour apart, newest first.</summary>
    private Task<(string Url, long FeedId, List<long> ArticleIds)> NewFeedWithArticlesAsync(int count, DateTime? newest = null) => DbAsync(async db =>
    {
        var url = $"https://reader-tests.example.com/{Guid.NewGuid():N}";
        var feed = new Feed { Url = url, Title = "Seeded", CreatedAt = DateTime.UtcNow, NextFetchAt = DateTime.UtcNow.AddDays(1) };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync(Ct);
        var start = newest ?? DateTime.UtcNow.AddMinutes(-1);
        var articles = Enumerable.Range(0, count).Select(i => new Article
        {
            FeedId = feed.Id,
            DedupKey = ArticleIdentity.DedupKey($"a{i}", null, null, null),
            Guid = $"a{i}",
            Title = $"Article {i}",
            ContentHash = "x",
            ContentHtml = $"<p>Body {i}</p>",
            PublishedAt = start.AddHours(-i),
            FetchedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        }).ToList();
        db.Articles.AddRange(articles);
        await db.SaveChangesAsync(Ct);
        return (url, feed.Id, articles.Select(a => a.Id).ToList());
    });

    private async Task<long> SubscribeAsync(Guid user, string url, params long[] folders)
    {
        var result = await Library.SubscribeAsync(user, url, folders, Ct);
        result.Succeeded.Should().BeTrue(result.Error);
        return result.Id!.Value;
    }

    // ---- Subscribing -------------------------------------------------------------------

    [Fact]
    public async Task A_new_url_creates_a_due_feed_and_a_second_subscriber_shares_it()
    {
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();
        var url = $"https://new-feed.example.com/{Guid.NewGuid():N}";

        await SubscribeAsync(alice, url);
        await SubscribeAsync(bob, url);
        (await Library.SubscribeAsync(bob, url, [], Ct)).Succeeded.Should().BeTrue("subscribing twice is harmless");

        var feeds = await DbAsync(db => db.Feeds.Where(f => f.Url == url).ToListAsync(Ct));
        feeds.Should().ContainSingle().Which.NextFetchAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1), "a new feed is due at once");
        (await DbAsync(db => db.Subscriptions.CountAsync(s => s.FeedId == feeds[0].Id, Ct))).Should().Be(2);
    }

    [Fact]
    public async Task Subscribing_to_an_existing_feed_backfills_the_200_newest_for_that_user_only()
    {
        var (url, feedId, articleIds) = await NewFeedWithArticlesAsync(205);
        var alice = await NewUserAsync();

        await SubscribeAsync(alice, url);

        var mine = await DbAsync(db => db.UserArticles.Where(u => u.FeedId == feedId).ToListAsync(Ct));
        mine.Should().HaveCount(LibraryService.BackfillArticles).And.OnlyContain(u => u.UserId == alice && !u.IsRead);
        mine.Select(u => u.ArticleId).Should().BeEquivalentTo(articleIds.Take(200), "the newest 200");
    }

    [Fact]
    public async Task Two_users_adding_the_same_new_url_at_once_share_one_feed()
    {
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();
        var url = $"https://race.example.com/{Guid.NewGuid():N}";

        var results = await Task.WhenAll(Library.SubscribeAsync(alice, url, [], Ct), Library.SubscribeAsync(bob, url, [], Ct));

        results.Should().OnlyContain(r => r.Succeeded);
        (await DbAsync(db => db.Feeds.CountAsync(f => f.Url == url, Ct))).Should().Be(1);
    }

    [Fact]
    public async Task Unsubscribing_keeps_saved_articles_but_drops_the_rest_and_stops_fetching()
    {
        var (url, feedId, articleIds) = await NewFeedWithArticlesAsync(3);
        var alice = await NewUserAsync();
        var subscription = await SubscribeAsync(alice, url);
        await Reading.SetSavedAsync(alice, articleIds[0], true, Ct);

        (await Library.UnsubscribeAsync(alice, subscription, Ct)).Succeeded.Should().BeTrue();

        (await Reading.GetArticlesAsync(alice, ReaderView.Saved, ArticleSort.Newest, false, null, Ct)).Items.Select(i => i.ArticleId).Should().Equal(articleIds[0]);
        (await Reading.GetArticlesAsync(alice, ReaderView.All, ArticleSort.Newest, false, null, Ct)).Items.Should().BeEmpty();
        var nav = await Reading.GetNavAsync(alice, Ct);
        nav.AllUnread.Should().Be(0, "a leftover saved article doesn't count as unread from a dropped source");
        nav.SavedCount.Should().Be(1);

        await DbAsync(db => db.Feeds.Where(f => f.Id == feedId).ExecuteUpdateAsync(s => s.SetProperty(f => f.NextFetchAt, DateTime.UtcNow.AddMinutes(-5)), Ct));
        var due = await DbAsync(db => db.Feeds.Where(f => f.NextFetchAt <= DateTime.UtcNow && f.Subscriptions.Any()).Select(f => f.Id).ToListAsync(Ct));
        due.Should().NotContain(feedId, "a feed nobody subscribes to isn't fetched");
    }

    // ---- Counts, views and paging ------------------------------------------------------

    [Fact]
    public async Task Nav_counts_folders_and_today()
    {
        var alice = await NewUserAsync();
        var news = (await Library.CreateFolderAsync(alice, "News", Ct)).Id!.Value;
        var tech = (await Library.CreateFolderAsync(alice, "Tech", Ct)).Id!.Value;
        var (urlA, _, idsA) = await NewFeedWithArticlesAsync(30); // 30 articles over 30 hours
        var (urlB, _, _) = await NewFeedWithArticlesAsync(2);
        var (urlC, _, _) = await NewFeedWithArticlesAsync(1);
        await SubscribeAsync(alice, urlA, news, tech);
        await SubscribeAsync(alice, urlB, news);
        await SubscribeAsync(alice, urlC);
        await Reading.SetReadAsync(alice, [idsA[0], idsA[1]], true, Ct);

        var nav = await Reading.GetNavAsync(alice, Ct);

        nav.AllUnread.Should().Be(28 + 2 + 1);
        nav.TodayUnread.Should().Be(22 + 2 + 1, "articles 2..23 of feed A are within 24 hours");
        nav.Folders.Select(f => (f.Name, f.Unread)).Should().Equal(("News", 30), ("Tech", 28));
        nav.Folders[0].Sources.Should().HaveCount(2);
        nav.Unfiled.Should().ContainSingle().Which.Unread.Should().Be(1);
    }

    [Theory]
    [InlineData(ArticleSort.Newest)]
    [InlineData(ArticleSort.Oldest)]
    public async Task Keyset_paging_returns_every_article_once_in_order(ArticleSort sort)
    {
        // Two articles share a timestamp to exercise the id tie-breaker.
        var (url, feedId, ids) = await NewFeedWithArticlesAsync(7);
        await DbAsync(db => db.Articles.Where(a => a.Id == ids[4]).ExecuteUpdateAsync(s => s.SetProperty(a => a.PublishedAt, a => a.PublishedAt.AddHours(1)), Ct));
        var alice = await NewUserAsync();
        var subscription = await SubscribeAsync(alice, url);

        var seen = new List<ArticleListItem>();
        ArticleCursor? cursor = null;
        do
        {
            var page = await Reading.GetArticlesAsync(alice, ReaderView.Source(subscription), sort, false, cursor, Ct, pageSize: 2);
            seen.AddRange(page.Items);
            cursor = page.Next;
        }
        while (cursor is not null);

        seen.Select(i => i.ArticleId).Should().OnlyHaveUniqueItems().And.HaveCount(7);
        var keys = seen.Select(i => (i.PublishedAt, i.ArticleId)).ToList();
        keys.Should().BeInAscendingOrder(sort == ArticleSort.Oldest ? Comparer<(DateTime, long)>.Default : Comparer<(DateTime, long)>.Create((a, b) => b.CompareTo(a)));
        _ = feedId;
    }

    [Fact]
    public async Task A_source_in_two_folders_appears_once_per_folder_view_and_once_in_all()
    {
        var alice = await NewUserAsync();
        var one = (await Library.CreateFolderAsync(alice, "One", Ct)).Id!.Value;
        var two = (await Library.CreateFolderAsync(alice, "Two", Ct)).Id!.Value;
        var (url, _, _) = await NewFeedWithArticlesAsync(3);
        await SubscribeAsync(alice, url, one, two);

        (await Reading.GetArticlesAsync(alice, ReaderView.Folder(one), ArticleSort.Newest, true, null, Ct)).Items.Should().HaveCount(3);
        (await Reading.GetArticlesAsync(alice, ReaderView.Folder(two), ArticleSort.Newest, true, null, Ct)).Items.Should().HaveCount(3);
        (await Reading.GetArticlesAsync(alice, ReaderView.All, ArticleSort.Newest, true, null, Ct)).Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task Mark_all_read_respects_view_and_age()
    {
        var alice = await NewUserAsync();
        var (url, _, _) = await NewFeedWithArticlesAsync(48); // spans 48 hours
        var (other, _, _) = await NewFeedWithArticlesAsync(5);
        var subscription = await SubscribeAsync(alice, url);
        await SubscribeAsync(alice, other);

        (await Reading.MarkAllReadAsync(alice, ReaderView.Source(subscription), TimeSpan.FromDays(1), Ct)).Should().Be(24, "articles 24..47 are over a day old");
        (await Reading.GetNavAsync(alice, Ct)).AllUnread.Should().Be(24 + 5);

        (await Reading.MarkAllReadAsync(alice, ReaderView.Source(subscription), null, Ct)).Should().Be(24);
        (await Reading.GetNavAsync(alice, Ct)).AllUnread.Should().Be(5, "the other source is untouched");
    }

    [Fact]
    public async Task Preferences_are_remembered_per_view()
    {
        var alice = await NewUserAsync();

        await Reading.SaveViewPreferenceAsync(alice, ReaderView.Today, ArticleLayout.Cards, ArticleSort.Oldest, false, Ct);
        await Reading.SetMarkReadModeAsync(alice, MarkReadMode.OnScroll, Ct);

        var today = await Reading.GetViewPreferenceAsync(alice, ReaderView.Today, Ct);
        (today.Layout, today.Sort, today.UnreadOnly).Should().Be((ArticleLayout.Cards, ArticleSort.Oldest, false));
        (await Reading.GetViewPreferenceAsync(alice, ReaderView.All, Ct)).Layout.Should().Be(ArticleLayout.Magazine, "other views keep the default");
        (await Reading.GetMarkReadModeAsync(alice, Ct)).Should().Be(MarkReadMode.OnScroll);
    }

    // ---- Isolation: Bob acting on Alice's ids changes nothing and sees nothing ----------

    [Fact]
    public async Task Another_user_cannot_read_or_change_anything_of_mine()
    {
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();
        var folder = (await Library.CreateFolderAsync(alice, "Private", Ct)).Id!.Value;
        var (url, _, ids) = await NewFeedWithArticlesAsync(3);
        var subscription = await SubscribeAsync(alice, url, folder);
        var (bobsUrl, _, _) = await NewFeedWithArticlesAsync(1);

        // Reads
        (await Reading.GetArticleAsync(bob, ids[0], Ct)).Should().BeNull();
        (await Reading.GetArticlesAsync(bob, ReaderView.Folder(folder), ArticleSort.Newest, false, null, Ct)).Items.Should().BeEmpty();
        (await Reading.GetArticlesAsync(bob, ReaderView.Source(subscription), ArticleSort.Newest, false, null, Ct)).Items.Should().BeEmpty();
        (await Library.ListSourcesAsync(bob, Ct)).Should().BeEmpty();
        (await Library.ListFoldersAsync(bob, Ct)).Should().BeEmpty();

        // Writes
        (await Reading.SetReadAsync(bob, ids, true, Ct)).Should().Be(0);
        (await Reading.SetSavedAsync(bob, ids[0], true, Ct)).Should().BeFalse();
        (await Reading.MarkAllReadAsync(bob, ReaderView.Folder(folder), null, Ct)).Should().Be(0);
        (await Reading.MarkAllReadAsync(bob, ReaderView.Source(subscription), null, Ct)).Should().Be(0);
        (await Library.UpdateSourceAsync(bob, subscription, "Hijacked", 5, [], Ct)).Succeeded.Should().BeFalse();
        (await Library.RefreshNowAsync(bob, subscription, Ct)).Succeeded.Should().BeFalse();
        (await Library.MoveSourceAsync(bob, folder, subscription, MoveDirection.Up, Ct)).Succeeded.Should().BeFalse();
        (await Library.UnsubscribeAsync(bob, subscription, Ct)).Succeeded.Should().BeFalse();
        (await Library.RenameFolderAsync(bob, folder, "Mine now", Ct)).Succeeded.Should().BeFalse();
        (await Library.MoveFolderAsync(bob, folder, MoveDirection.Down, Ct)).Succeeded.Should().BeFalse();
        (await Library.DeleteFolderAsync(bob, folder, Ct)).Succeeded.Should().BeFalse();
        var bobsSubscription = await SubscribeAsync(bob, bobsUrl, folder);
        (await Library.UpdateSourceAsync(bob, bobsSubscription, null, null, [folder], Ct)).Succeeded.Should().BeTrue();

        // Nothing of Alice's changed, and Bob's source didn't land in her folder.
        var nav = await Reading.GetNavAsync(alice, Ct);
        nav.AllUnread.Should().Be(3);
        nav.SavedCount.Should().Be(0);
        nav.Folders.Should().ContainSingle().Which.Name.Should().Be("Private");
        nav.Folders[0].Sources.Should().ContainSingle().Which.Title.Should().Be("Seeded");
        (await DbAsync(db => db.SubscriptionFolders.CountAsync(sf => sf.FolderId == folder, Ct))).Should().Be(1);
    }
}

public sealed class SqliteReaderServiceTests(SqliteAppFixture fixture) : ReaderServiceTests(fixture), IClassFixture<SqliteAppFixture>;

public sealed class PostgresReaderServiceTests(PostgresAppFixture fixture) : ReaderServiceTests(fixture), IClassFixture<PostgresAppFixture>;
