using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Reading;

/// <summary>
/// Full-text matching. Two real implementations: SQLite FTS5 and Postgres tsvector (PLAN D51).
/// Returns a composable query of article ids so callers can scope it to one user's articles.
/// Implementations must accept any user input without throwing.
/// </summary>
public interface IArticleSearch
{
    /// <summary>
    /// Only the newest this many matching articles are considered (PLAN D56). A word in every
    /// article would otherwise make search scan the whole index (360 ms at 200k articles).
    /// </summary>
    public const int MaxCandidates = 5000;

    IQueryable<long> MatchingArticleIds(SkimrDbContext db, string query);
}
