using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Skimr.Domain;

namespace Skimr.Infrastructure.Persistence;

public sealed class SkimrDbContext(DbContextOptions<SkimrDbContext> options) : DbContext(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<Feed> Feeds => Set<Feed>();

    public DbSet<Article> Articles => Set<Article>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<UserArticle> UserArticles => Set<UserArticle>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // All timestamps are UTC (PLAN D2). SQLite reads them back as Unspecified and
        // Npgsql refuses non-UTC values for timestamptz, so pin the Kind both ways.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Value).HasMaxLength(4000);
        });

        modelBuilder.Entity<Feed>(e =>
        {
            e.Property(x => x.Url).HasMaxLength(2048);
            e.HasIndex(x => x.Url).IsUnique();
            e.Property(x => x.SiteUrl).HasMaxLength(2048);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.ETag).HasMaxLength(500);
            e.Property(x => x.LastModified).HasMaxLength(100);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.HubUrl).HasMaxLength(2048);
            e.Property(x => x.SelfUrl).HasMaxLength(2048);
            e.Property(x => x.WebSubSecret).HasMaxLength(200);
            e.HasIndex(x => x.NextFetchAt);
        });

        modelBuilder.Entity<Article>(e =>
        {
            e.Property(x => x.DedupKey).HasMaxLength(64);
            e.HasIndex(x => new { x.FeedId, x.DedupKey }).IsUnique();
            e.HasIndex(x => new { x.FeedId, x.PublishedAt });
            e.HasIndex(x => x.LastSeenAt);
            e.Property(x => x.Guid).HasMaxLength(2048);
            e.Property(x => x.Url).HasMaxLength(2048);
            e.Property(x => x.Title).HasMaxLength(1000);
            e.Property(x => x.Author).HasMaxLength(500);
            e.Property(x => x.Snippet).HasMaxLength(500);
            e.Property(x => x.ThumbnailUrl).HasMaxLength(2048);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.HasOne(x => x.Feed).WithMany().HasForeignKey(x => x.FeedId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Subscription>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.FeedId }).IsUnique();
            e.HasIndex(x => x.FeedId);
            e.Property(x => x.CustomTitle).HasMaxLength(500);
            e.HasOne(x => x.Feed).WithMany(f => f.Subscriptions).HasForeignKey(x => x.FeedId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserArticle>(e =>
        {
            e.HasKey(x => new { x.UserId, x.ArticleId });
            e.HasIndex(x => new { x.UserId, x.IsRead, x.FeedId });
            e.HasIndex(x => new { x.UserId, x.FeedId, x.PublishedAt, x.ArticleId });
            e.HasIndex(x => new { x.UserId, x.PublishedAt, x.ArticleId });
            e.HasIndex(x => new { x.UserId, x.IsSaved, x.SavedAt });
            e.HasIndex(x => x.ArticleId);
            e.HasOne(x => x.Article).WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
