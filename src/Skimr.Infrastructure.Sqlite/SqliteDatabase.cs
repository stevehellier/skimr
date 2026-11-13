using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Sqlite;

public static class SqliteDatabase
{
    public static DbContextOptionsBuilder UseSkimrSqlite(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlite(connectionString, o => o.MigrationsAssembly(typeof(SqliteDatabase).Assembly.GetName().Name));
}

internal sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SkimrDbContext>
{
    public SkimrDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SkimrDbContext>();
        builder.UseSkimrSqlite("Data Source=design-time.db");
        return new SkimrDbContext(builder.Options);
    }
}
