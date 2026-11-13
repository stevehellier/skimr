using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Reading;

/// <summary>
/// Full-text matching. Two real implementations: SQLite FTS5 and Postgres tsvector (PLAN D51).
/// Returns a composable query of article ids so callers can scope it to one user's articles.
/// Implementations must accept any user input without throwing.
/// </summary>
public interface IArticleSearch
{
    /// <param name="limit">Only the newest this many matches, or all when null (PLAN D56).</param>
    IQueryable<long> MatchingArticleIds(SkimrDbContext db, string query, int? limit);
}

/// <summary>
/// An unfiltered search considers only the newest <see cref="CandidateLimit"/> matching articles:
/// a word in every article otherwise scans the whole index (360 ms at 200k, 11 ms capped).
/// Filtered searches (source, folder, dates, read state) are never capped, because the cap is
/// global and would silently drop older or quieter matches.
/// </summary>
public sealed record SearchOptions(int CandidateLimit = 5000)
{
    public static readonly SearchOptions Default = new();
}
