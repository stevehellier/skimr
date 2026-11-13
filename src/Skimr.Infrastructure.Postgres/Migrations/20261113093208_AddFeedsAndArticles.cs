using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Skimr.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedsAndArticles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Feeds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SiteUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ETag = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastModified = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastFetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    NextFetchAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HubUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SelfUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    WebSubSecret = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WebSubLeaseExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Feeds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Articles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FeedId = table.Column<long>(type: "bigint", nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Guid = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Author = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ContentHtml = table.Column<string>(type: "text", nullable: true),
                    Snippet = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ThumbnailUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FullTextHtml = table.Column<string>(type: "text", nullable: true),
                    FullTextFetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Articles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Articles_Feeds_FeedId",
                        column: x => x.FeedId,
                        principalTable: "Feeds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeedId = table.Column<long>(type: "bigint", nullable: false),
                    CustomTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RefreshMinutes = table.Column<int>(type: "integer", nullable: true),
                    AlwaysFetchFullText = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Feeds_FeedId",
                        column: x => x.FeedId,
                        principalTable: "Feeds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserArticles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArticleId = table.Column<long>(type: "bigint", nullable: false),
                    FeedId = table.Column<long>(type: "bigint", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsSaved = table.Column<bool>(type: "boolean", nullable: false),
                    SavedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserArticles", x => new { x.UserId, x.ArticleId });
                    table.ForeignKey(
                        name: "FK_UserArticles_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Articles_FeedId_DedupKey",
                table: "Articles",
                columns: new[] { "FeedId", "DedupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Articles_FeedId_PublishedAt",
                table: "Articles",
                columns: new[] { "FeedId", "PublishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Articles_LastSeenAt",
                table: "Articles",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Feeds_NextFetchAt",
                table: "Feeds",
                column: "NextFetchAt");

            migrationBuilder.CreateIndex(
                name: "IX_Feeds_Url",
                table: "Feeds",
                column: "Url",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_FeedId",
                table: "Subscriptions",
                column: "FeedId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_UserId_FeedId",
                table: "Subscriptions",
                columns: new[] { "UserId", "FeedId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserArticles_ArticleId",
                table: "UserArticles",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserArticles_UserId_FeedId_PublishedAt_ArticleId",
                table: "UserArticles",
                columns: new[] { "UserId", "FeedId", "PublishedAt", "ArticleId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserArticles_UserId_IsRead_FeedId",
                table: "UserArticles",
                columns: new[] { "UserId", "IsRead", "FeedId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserArticles_UserId_IsSaved_SavedAt",
                table: "UserArticles",
                columns: new[] { "UserId", "IsSaved", "SavedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserArticles_UserId_PublishedAt_ArticleId",
                table: "UserArticles",
                columns: new[] { "UserId", "PublishedAt", "ArticleId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Subscriptions");

            migrationBuilder.DropTable(
                name: "UserArticles");

            migrationBuilder.DropTable(
                name: "Articles");

            migrationBuilder.DropTable(
                name: "Feeds");
        }
    }
}
