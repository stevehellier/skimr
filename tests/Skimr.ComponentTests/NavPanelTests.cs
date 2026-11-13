using Skimr.Infrastructure.Reading;
using Skimr.Web.Components.Reader;

namespace Skimr.ComponentTests;

public sealed class NavPanelTests : BunitContext
{
    private static readonly NavModel Nav = new(
        AllUnread: 42,
        TodayUnread: 7,
        SavedCount: 0,
        Folders:
        [
            new NavFolder(1, "News", 30, [new NavSource(10, 100, "BBC", 25, false), new NavSource(11, 101, "Guardian", 5, true)]),
            new NavFolder(2, "Quiet", 0, [new NavSource(12, 102, "Old blog", 0, false)]),
        ],
        Unfiled: [new NavSource(13, 103, "Loose source", 12, false)]);

    private IRenderedComponent<NavPanel> Render(ReaderView current) =>
        Render<NavPanel>(p => p.Add(x => x.Nav, Nav).Add(x => x.Current, current));

    private static string? CountFor(IRenderedComponent<NavPanel> nav, string label) =>
        nav.FindAll("a.nav-link").Single(a => a.QuerySelector(".label")!.TextContent == label).QuerySelector(".count")?.TextContent;

    [Fact]
    public void Shows_unread_counts_for_views_folders_and_sources()
    {
        var nav = Render(ReaderView.All);

        CountFor(nav, "All").Should().Be("42");
        CountFor(nav, "Today").Should().Be("7");
        CountFor(nav, "News").Should().Be("30");
        CountFor(nav, "BBC").Should().Be("25");
        CountFor(nav, "Loose source").Should().Be("12");
    }

    [Fact]
    public void Zero_counts_are_not_shown()
    {
        var nav = Render(ReaderView.All);

        CountFor(nav, "Read Later").Should().BeNull();
        CountFor(nav, "Quiet").Should().BeNull();
        CountFor(nav, "Old blog").Should().BeNull();
    }

    [Fact]
    public void Counts_have_an_accessible_label()
    {
        Render(ReaderView.All).Find("a[href='today'] .count").GetAttribute("aria-label").Should().Be("7 unread");
    }

    [Fact]
    public void The_current_view_is_marked()
    {
        var nav = Render(ReaderView.Source(11));

        var current = nav.FindAll("[aria-current='page']").Should().ContainSingle().Subject;
        current.GetAttribute("href").Should().Be("source/11");
    }

    [Fact]
    public void Sources_are_listed_under_their_folders_with_links_and_error_markers()
    {
        var nav = Render(ReaderView.All);

        var news = nav.FindAll("details.nav-folder")[0];
        news.QuerySelectorAll(".sources a.nav-link").Select(a => a.GetAttribute("href")).Should().Equal("source/10", "source/11");
        nav.FindAll(".source-error").Should().ContainSingle("only the Guardian failed")
            .Which.GetAttribute("aria-label").Should().Be("Fetch error");
    }

    [Fact]
    public void Add_source_raises_the_callback()
    {
        var clicked = false;
        var nav = Render<NavPanel>(p => p.Add(x => x.Nav, Nav).Add(x => x.OnAddSource, () => clicked = true));

        nav.Find("button").Click();

        clicked.Should().BeTrue();
    }
}
