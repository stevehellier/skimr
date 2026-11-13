namespace Skimr.Infrastructure.Sqlite;

public static class SqliteMaintenance
{
    /// <summary>
    /// SQLite's recommended statistics refresh: analyse only tables whose statistics are stale.
    /// Full analysis, not a bounded sample: with analysis_limit=1000 the planner misjudged the
    /// partial indexes and the Read Later count went from 1 ms to 85 ms (results/phase5-perf).
    /// </summary>
    public const string Optimize = "PRAGMA optimize=0x10002;";
}
