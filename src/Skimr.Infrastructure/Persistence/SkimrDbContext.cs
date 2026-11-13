using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Skimr.Domain;
using Skimr.Infrastructure.Identity;

namespace Skimr.Infrastructure.Persistence;

public sealed class SkimrDbContext(DbContextOptions<SkimrDbContext> options)
    : IdentityDbContext<SkimrUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    public DbSet<Invite> Invites => Set<Invite>();

    public DbSet<Folder> Folders => Set<Folder>();

    public DbSet<SubscriptionFolder> SubscriptionFolders => Set<SubscriptionFolder>();

    public DbSet<UserPreferences> UserPreferences => Set<UserPreferences>();

    public DbSet<ViewPreference> ViewPreferences => Set<ViewPreference>();

    public DbSet<Board> Boards => Set<Board>();

    public DbSet<BoardArticle> BoardArticles => Set<BoardArticle>();

    public DbSet<Icon> Icons => Set<Icon>();

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
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ApiToken>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Prefix).HasMaxLength(16);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.FeverKeyHash).HasMaxLength(64);
            e.HasIndex(x => x.FeverKeyHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<SkimrUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Invite>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasOne<SkimrUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<SkimrUser>().WithMany().HasForeignKey(x => x.UsedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Folder>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
            e.HasOne<SkimrUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SubscriptionFolder>(e =>
        {
            e.HasKey(x => new { x.SubscriptionId, x.FolderId });
            e.HasIndex(x => x.FolderId);
            e.HasOne(x => x.Subscription).WithMany(s => s.Folders).HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Folder).WithMany(f => f.Subscriptions).HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserPreferences>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.MarkReadMode).HasConversion<string>().HasMaxLength(20);
            e.HasOne<SkimrUser>().WithOne().HasForeignKey<UserPreferences>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ViewPreference>(e =>
        {
            e.HasKey(x => new { x.UserId, x.ViewKey });
            e.Property(x => x.ViewKey).HasMaxLength(50);
            e.Property(x => x.Layout).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Sort).HasConversion<string>().HasMaxLength(20);
            e.HasOne<SkimrUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Board>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
            e.HasOne<SkimrUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BoardArticle>(e =>
        {
            e.HasKey(x => new { x.BoardId, x.ArticleId });
            e.HasIndex(x => x.ArticleId);
            e.HasOne(x => x.Board).WithMany(b => b.Articles).HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Icon>(e =>
        {
            e.Property(x => x.Hash).HasMaxLength(64);
            e.HasIndex(x => x.Hash).IsUnique();
            e.Property(x => x.MimeType).HasMaxLength(50);
            e.Property(x => x.FileName).HasMaxLength(100);
        });

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
            e.HasOne<Icon>().WithMany().HasForeignKey(x => x.IconId).OnDelete(DeleteBehavior.SetNull);
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
            e.Property(x => x.SearchText).HasMaxLength(20000);
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
            e.HasOne<SkimrUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserArticle>(e =>
        {
            e.HasKey(x => new { x.UserId, x.ArticleId });
            // Unread counts (PLAN D1, D57): a partial index over unread rows only. With IsRead as a
            // key column, SQLite judged the boolean unselective, picked another index and read every
            // row: 300 ms at 200k articles. The filter must read exactly as EF writes !IsRead, or the
            // planner won't use the index (SQLite matches the WHERE clause by form).
            e.HasIndex(x => new { x.UserId, x.FeedId }, "IX_UserArticles_Unread").HasFilter("NOT \"IsRead\"");
            // A second one in date order for unread-only pages; with only the first, SQLite read all
            // unread rows and sorted them (100 ms) instead of walking by date (1 ms).
            e.HasIndex(x => new { x.UserId, x.PublishedAt, x.ArticleId, x.FeedId }, "IX_UserArticles_UnreadByDate").HasFilter("NOT \"IsRead\"");
            e.HasIndex(x => new { x.UserId, x.FeedId, x.PublishedAt, x.ArticleId });
            e.HasIndex(x => new { x.UserId, x.PublishedAt, x.ArticleId });
            // Read Later: partial, for the same reason as the unread ones (a boolean key column is ignored).
            e.HasIndex(x => new { x.UserId, x.PublishedAt, x.ArticleId }, "IX_UserArticles_Saved").HasFilter("\"IsSaved\"");
            e.HasIndex(x => x.ArticleId);
            e.HasOne(x => x.Article).WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<SkimrUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
