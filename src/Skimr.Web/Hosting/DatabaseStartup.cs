using Microsoft.EntityFrameworkCore;
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
            await db.Database.MigrateAsync(cancellationToken);
        }
    }
}
