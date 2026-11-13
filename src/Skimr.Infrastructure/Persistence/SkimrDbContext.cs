using Microsoft.EntityFrameworkCore;
using Skimr.Domain;

namespace Skimr.Infrastructure.Persistence;

public sealed class SkimrDbContext(DbContextOptions<SkimrDbContext> options) : DbContext(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Value).HasMaxLength(4000);
        });
    }
}
