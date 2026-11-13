using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;
using Skimr.IntegrationTests.Infrastructure;

namespace Skimr.IntegrationTests;

public abstract class OpmlTests(AppFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => fixture.Factory.Services;

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private Task<Guid> NewUserAsync() => InScopeAsync(sp => TestData.AddUserAsync(sp.GetRequiredService<SkimrDbContext>(), Ct));

    private Task<OpmlImportResult> ImportAsync(Guid user, string opml) =>
        InScopeAsync(sp => sp.GetRequiredService<OpmlService>().ImportAsync(user, Encoding.UTF8.GetBytes(opml), Ct));

    private Task<string> ExportAsync(Guid user) => InScopeAsync(sp => sp.GetRequiredService<OpmlService>().ExportAsync(user, Ct));

    /// <summary>Folder name → sorted feed URLs; "" for sources in no folder.</summary>
    private async Task<Dictionary<string, List<string>>> StructureAsync(Guid user)
    {
        var sources = await InScopeAsync(sp => sp.GetRequiredService<LibraryService>().ListSourcesAsync(user, Ct));
        var folders = await InScopeAsync(sp => sp.GetRequiredService<LibraryService>().ListFoldersAsync(user, Ct));
        var result = folders.ToDictionary(f => f.Name, f => sources.Where(s => s.FolderIds.Contains(f.Id)).Select(s => s.FeedUrl).Order().ToList());
        result[""] = sources.Where(s => s.FolderIds.Count == 0).Select(s => s.FeedUrl).Order().ToList();
        return result;
    }

    private static string Url(string id) => $"https://opml-tests.example.com/{id}";

    [Fact]
    public async Task Import_flattens_nested_folders_and_tolerates_real_world_variants()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var opml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <opml version="1.0">
              <head><title>From another reader</title></head>
              <body>
                <outline text="Tech" title="Tech">
                  <outline type="rss" text="A" xmlUrl="{Url(token + "a")}" htmlUrl="https://a.example.com/"/>
                  <outline text="Rust">
                    <outline text="B" xmlurl="{Url(token + "b")}"/>
                  </outline>
                </outline>
                <outline title="News">
                  <outline type="rss" title="A again" xmlUrl="{Url(token + "a")}"/>
                </outline>
                <outline type="rss" text="Loose" XMLURL="{Url(token + "c")}"/>
                <outline text="Empty folder"/>
              </body>
            </opml>
            """;
        var alice = await NewUserAsync();

        var result = await ImportAsync(alice, opml);

        result.Added.Should().Be(3);
        result.AlreadySubscribed.Should().Be(1, "feed A appears in two folders");
        result.Problems.Should().BeEmpty();
        var structure = await StructureAsync(alice);
        structure["Tech"].Should().Equal(Url(token + "a"));
        structure["Tech / Rust"].Should().Equal(Url(token + "b"));
        structure["News"].Should().Equal(Url(token + "a"));
        structure[""].Should().Equal(Url(token + "c"));

        var titles = await InScopeAsync(sp => sp.GetRequiredService<SkimrDbContext>().Feeds.Where(f => f.Url.StartsWith(Url(token))).Select(f => f.Title).ToListAsync(Ct));
        titles.Should().Contain(["A", "B", "Loose"], "OPML titles show until the first fetch");
    }

    [Fact]
    public async Task Export_then_import_reproduces_the_same_subscriptions_and_folders()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var alice = await NewUserAsync();
        var library = Services.GetRequiredService<LibraryService>();
        var tech = (await library.CreateFolderAsync(alice, "Tech", Ct)).Id!.Value;
        var news = (await library.CreateFolderAsync(alice, "News & <Views>", Ct)).Id!.Value;
        await library.SubscribeAsync(alice, Url(token + "1"), [tech, news], Ct);
        await library.SubscribeAsync(alice, Url(token + "2"), [news], Ct);
        await library.SubscribeAsync(alice, Url(token + "3"), [], Ct);

        var exported = await ExportAsync(alice);
        var bob = await NewUserAsync();
        var result = await ImportAsync(bob, exported);

        result.Problems.Should().BeEmpty();
        (await StructureAsync(bob)).Should().BeEquivalentTo(await StructureAsync(alice));
        XDocument.Parse(exported).Descendants("outline").Count(o => (string?)o.Attribute("xmlUrl") == Url(token + "1"))
            .Should().Be(2, "a source in two folders is listed under each");
    }

    [Fact]
    public async Task Importing_twice_changes_nothing()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var opml = $"""<opml version="2.0"><body><outline text="F"><outline text="X" xmlUrl="{Url(token)}"/></outline></body></opml>""";
        var alice = await NewUserAsync();

        await ImportAsync(alice, opml);
        var again = await ImportAsync(alice, opml);

        (again.Added, again.AlreadySubscribed, again.FoldersCreated).Should().Be((0, 1, 0));
        (await StructureAsync(alice))["F"].Should().ContainSingle();
    }

    [Theory]
    [InlineData("<html><body>not opml</body></html>", "isn't OPML")]
    [InlineData("", "isn't valid OPML")]
    [InlineData("{\"json\": true}", "isn't valid OPML")]
    public async Task Non_opml_files_are_reported_not_thrown(string content, string message)
    {
        var result = await ImportAsync(await NewUserAsync(), content);

        result.Added.Should().Be(0);
        result.Problems.Should().ContainSingle().Which.Should().Contain(message);
    }

    [Fact]
    public async Task External_entities_in_opml_cannot_read_files()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"skimr-opml-xxe-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(secret, "TOP-SECRET", Ct);
        try
        {
            var opml = $"""<?xml version="1.0"?><!DOCTYPE opml [<!ENTITY xxe SYSTEM "file:///{secret.Replace('\\', '/')}">]><opml version="2.0"><body><outline text="&xxe;"><outline text="x" xmlUrl="{Url(Guid.NewGuid().ToString("N"))}"/></outline></body></opml>""";
            var alice = await NewUserAsync();

            await ImportAsync(alice, opml);

            (await Services.GetRequiredService<LibraryService>().ListFoldersAsync(alice, Ct)).Select(f => f.Name).Should().NotContain(n => n.Contains("TOP-SECRET"));
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public async Task Importing_150_feeds_is_quick()
    {
        // "Import an OPML file and read articles within 2 minutes": the import itself must be seconds.
        var token = Guid.NewGuid().ToString("N")[..8];
        var outlines = string.Concat(Enumerable.Range(0, 150).Select(i => $"""<outline text="F{i % 10}"><outline text="S{i}" xmlUrl="{Url($"{token}-{i}")}"/></outline>"""));
        var alice = await NewUserAsync();

        var timer = Stopwatch.StartNew();
        var result = await ImportAsync(alice, $"<opml version=\"2.0\"><body>{outlines}</body></opml>");
        timer.Stop();

        result.Added.Should().Be(150);
        TestContext.Current.SendDiagnosticMessage($"Imported 150 feeds in {timer.ElapsedMilliseconds} ms");
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        // Compare with a time taken here: DateTime.UtcNow inside a query runs on the database's clock.
        var now = DateTime.UtcNow.AddSeconds(1);
        (await InScopeAsync(sp => sp.GetRequiredService<SkimrDbContext>().Feeds.CountAsync(f => f.Url.StartsWith(Url(token)) && f.NextFetchAt <= now, Ct)))
            .Should().Be(150, "every new feed is due, so the scheduler fetches them without the user waiting");
    }
}

public sealed class SqliteOpmlTests(SqliteAppFixture fixture) : OpmlTests(fixture), IClassFixture<SqliteAppFixture>;

public sealed class PostgresOpmlTests(PostgresAppFixture fixture) : OpmlTests(fixture), IClassFixture<PostgresAppFixture>;
