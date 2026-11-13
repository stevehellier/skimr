using System.Text;
using Microsoft.EntityFrameworkCore;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;

namespace Skimr.Infrastructure.Sqlite;

/// <summary>FTS5 over the ArticleSearch external-content table kept in sync by triggers.</summary>
public sealed class SqliteArticleSearch : IArticleSearch
{
    private const int MaxTerms = 12;

    public IQueryable<long> MatchingArticleIds(SkimrDbContext db, string query, int? limit)
    {
        var match = ToFtsQuery(query);
        return match is null
            ? db.Articles.Where(_ => false).Select(a => a.Id)
            : limit is { } max
                ? db.Database.SqlQuery<long>($"SELECT rowid AS \"Value\" FROM ArticleSearch WHERE ArticleSearch MATCH {match} ORDER BY rowid DESC LIMIT {max}")
                : db.Database.SqlQuery<long>($"SELECT rowid AS \"Value\" FROM ArticleSearch WHERE ArticleSearch MATCH {match}");
    }

    /// <summary>
    /// Every term becomes a quoted string, so FTS5 operators and syntax in user input
    /// ("NEAR(", "-", "AND", stray quotes, "column:") are searched for literally instead of
    /// causing a syntax error. A trailing * keeps prefix search. Terms are ANDed.
    /// </summary>
    internal static string? ToFtsQuery(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var terms = new List<string>();
        foreach (var raw in input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(MaxTerms))
        {
            var prefix = raw.EndsWith('*');
            var text = new string(raw.TrimEnd('*').Where(c => !char.IsControl(c)).ToArray());
            if (text.Length == 0)
            {
                continue;
            }

            var quoted = new StringBuilder("\"").Append(text.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            if (prefix)
            {
                quoted.Append('*');
            }

            terms.Add(quoted.ToString());
        }

        return terms.Count == 0 ? null : string.Join(' ', terms);
    }
}
