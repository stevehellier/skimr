using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Skimr.Infrastructure.Persistence;
using Skimr.Web.Configuration;

namespace Skimr.Web.Hosting;

internal static class DatabaseStartup
{
    public static async Task InitialiseAsync(IServiceProvider services, SkimrSettings settings, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();

        if (settings.Provider == DatabaseProvider.Sqlite)
        {
            // WAL lets the UI read while fetch workers write. The setting persists in the file.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        }

        if (settings.AutoMigrate)
        {
            // Exists() probes with errors expected, so a fresh Postgres server doesn't log a
            // connection ERR before the database is created.
            var creator = db.GetService<IRelationalDatabaseCreator>();
            if (!await creator.ExistsAsync(cancellationToken))
            {
                await creator.CreateAsync(cancellationToken);
            }

            await db.Database.MigrateAsync(cancellationToken);
        }
    }
}
