using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Postgres;

public static class PostgresDatabase
{
    public static DbContextOptionsBuilder UseSkimrPostgres(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, o => o.MigrationsAssembly(typeof(PostgresDatabase).Assembly.GetName().Name));
}

internal sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<SkimrDbContext>
{
    // Migrations are generated without connecting, so this connection string is never used.
    public SkimrDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SkimrDbContext>();
        builder.UseSkimrPostgres("Host=localhost;Database=skimr_design");
        return new SkimrDbContext(builder.Options);
    }
}
