using Microsoft.EntityFrameworkCore;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;

namespace Skimr.Infrastructure.Postgres;

/// <summary>
/// tsvector matching. The expression must stay identical to the IX_Articles_Search index
/// (to_tsvector('simple', "Title" || ' ' || COALESCE("SearchText", ''))) or the planner won't use it.
/// Plain queries go through websearch_to_tsquery, which accepts any input. Prefix queries
/// ("kest*") aren't supported there, so they're built from terms reduced to letters and digits,
/// which can't break to_tsquery's syntax.
/// </summary>
public sealed class PostgresArticleSearch : IArticleSearch
{
    private const int MaxTerms = 12;

    public IQueryable<long> MatchingArticleIds(SkimrDbContext db, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return db.Articles.Where(_ => false).Select(a => a.Id);
        }

        if (!query.Contains('*', StringComparison.Ordinal))
        {
            return db.Articles
                .Where(a => EF.Functions.ToTsVector("simple", a.Title + " " + (a.SearchText ?? ""))
                    .Matches(EF.Functions.WebSearchToTsQuery("simple", query)))
                .OrderByDescending(a => a.Id)
                .Take(IArticleSearch.MaxCandidates)
                .Select(a => a.Id);
        }

        var prefixQuery = ToPrefixQuery(query);
        return prefixQuery is null
            ? db.Articles.Where(_ => false).Select(a => a.Id)
            : db.Articles
                .Where(a => EF.Functions.ToTsVector("simple", a.Title + " " + (a.SearchText ?? ""))
                    .Matches(EF.Functions.ToTsQuery("simple", prefixQuery)))
                .OrderByDescending(a => a.Id)
                .Take(IArticleSearch.MaxCandidates)
                .Select(a => a.Id);
    }

    internal static string? ToPrefixQuery(string input)
    {
        var terms = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Take(MaxTerms)
            .Select(raw => (Text: new string(raw.Where(char.IsLetterOrDigit).ToArray()), Prefix: raw.EndsWith('*')))
            .Where(t => t.Text.Length > 0)
            .Select(t => t.Prefix ? t.Text + ":*" : t.Text)
            .ToList();
        return terms.Count == 0 ? null : string.Join(" & ", terms);
    }
}
