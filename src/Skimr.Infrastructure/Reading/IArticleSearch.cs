using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Reading;

/// <summary>
/// Full-text matching. Two real implementations: SQLite FTS5 and Postgres tsvector (PLAN D51).
/// Returns a composable query of article ids so callers can scope it to one user's articles.
/// Implementations must accept any user input without throwing.
/// </summary>
public interface IArticleSearch
{
    IQueryable<long> MatchingArticleIds(SkimrDbContext db, string query);
}
