using Skimr.Domain;
using Skimr.Infrastructure.Reading;
using Skimr.Web.Components.Reader;

namespace Skimr.ComponentTests;

public sealed class ArticleListTests : BunitContext
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private static readonly ArticleListItem[] Items =
    [
        new(1, "BBC", "Unread with image", "A snippet", "https://img.example/1.jpg", "https://bbc.example/1", null, Now.AddHours(-3), IsRead: false, IsSaved: false),
        new(2, "BBC", "Read and saved", "Another", null, "https://bbc.example/2", "Ann", Now.AddDays(-2), IsRead: true, IsSaved: true),
    ];

    private IRenderedComponent<ArticleList> Render(ArticleLayout layout, IReadOnlyList<ArticleListItem>? items = null, long? selected = null) =>
        Render<ArticleList>(p => p
            .Add(x => x.Items, items ?? Items)
            .Add(x => x.Layout, layout)
            .Add(x => x.Now, Now)
            .Add(x => x.SelectedId, selected));

    [Fact]
    public void Magazine_shows_thumbnail_snippet_and_age()
    {
        var list = Render(ArticleLayout.Magazine);

        list.Find("ul").ClassList.Should().Contain("layout-magazine");
        list.FindAll("img.thumb").Should().ContainSingle().Which.GetAttribute("referrerpolicy").Should().Be("no-referrer");
        list.FindAll(".snippet").Select(s => s.TextContent).Should().Equal("A snippet", "Another");
        list.FindAll("time").Select(t => t.TextContent).Should().Equal("3h", "2d");
    }

    [Fact]
    public void Title_only_hides_images_and_snippets()
    {
        var list = Render(ArticleLayout.TitleOnly);

        list.Find("ul").ClassList.Should().Contain("layout-titleonly");
        list.FindAll("img").Should().BeEmpty();
        list.FindAll(".snippet").Should().BeEmpty();
        list.FindAll(".title").Select(t => t.TextContent).Should().Equal("Unread with image", "Read and saved");
    }

    [Fact]
    public void Cards_layout_uses_the_cards_class()
    {
        Render(ArticleLayout.Cards).Find("ul").ClassList.Should().Contain("layout-cards");
    }

    [Fact]
    public void Read_saved_and_selected_states_are_exposed()
    {
        var list = Render(ArticleLayout.Magazine, selected: 2);

        var items = list.FindAll("li.article-item");
        items[0].ClassList.Should().Contain("unread");
        items[1].ClassList.Should().Contain("read").And.Contain("selected");
        items[1].QuerySelector("button.open")!.GetAttribute("aria-current").Should().Be("true");
        items[1].QuerySelectorAll(".item-actions button")[0].GetAttribute("aria-pressed").Should().Be("true", "saved");
        items[0].QuerySelectorAll(".item-actions button")[1].GetAttribute("aria-pressed").Should().Be("false", "unread");
        items[0].GetAttribute("data-article-id").Should().Be("1", "reader.js uses this to mark read on scroll");
    }

    [Fact]
    public void Buttons_raise_open_save_and_read_callbacks_with_the_article_id()
    {
        long? opened = null, saved = null, read = null;
        var list = Render<ArticleList>(p => p
            .Add(x => x.Items, Items)
            .Add(x => x.OnOpen, id => opened = id)
            .Add(x => x.OnToggleSaved, id => saved = id)
            .Add(x => x.OnToggleRead, id => read = id));

        list.FindAll("button.open")[1].Click();
        list.FindAll(".item-actions button")[0].Click();
        list.FindAll(".item-actions button")[3].Click();

        (opened, saved, read).Should().Be((2L, 1L, 2L));
    }

    [Fact]
    public void Empty_list_shows_the_empty_text()
    {
        var list = Render<ArticleList>(p => p.Add(x => x.Items, Array.Empty<ArticleListItem>()).Add(x => x.EmptyText, "All caught up."));

        list.Find(".empty").TextContent.Should().Be("All caught up.");
        list.FindAll("ul").Should().BeEmpty();
    }
}
