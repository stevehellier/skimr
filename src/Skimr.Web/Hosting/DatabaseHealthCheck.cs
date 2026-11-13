using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Web.Hosting;

internal sealed class DatabaseHealthCheck(SkimrDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            return HealthCheckResult.Unhealthy("Cannot connect to the database.");
        }

        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        return pending.Any()
            ? HealthCheckResult.Unhealthy("Database has pending migrations. Enable Database:AutoMigrate or apply them manually.")
            : HealthCheckResult.Healthy();
    }
}
