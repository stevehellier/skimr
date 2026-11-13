using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Skimr.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddUnreadAndSavedIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserArticles_UserId_IsRead_FeedId",
                table: "UserArticles");

            migrationBuilder.DropIndex(
                name: "IX_UserArticles_UserId_IsSaved_SavedAt",
                table: "UserArticles");

            migrationBuilder.CreateIndex(
                name: "IX_UserArticles_Saved",
                table: "UserArticles",
                columns: new[] { "UserId", "PublishedAt", "ArticleId" },
                filter: "\"IsSaved\"");

            migrationBuilder.CreateIndex(
                name: "IX_UserArticles_Unread",
                table: "UserArticles",
                columns: new[] { "UserId", "FeedId" },
                filter: "NOT \"IsRead\"");

            migrationBuilder.CreateIndex(
                name: "IX_UserArticles_UnreadByDate",
                table: "UserArticles",
                columns: new[] { "UserId", "PublishedAt", "ArticleId", "FeedId" },
                filter: "NOT \"IsRead\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserArticles_Saved",
                table: "UserArticles");

            migrationBuilder.DropIndex(
                name: "IX_UserArticles_Unread",
                table: "UserArticles");

            migrationBuilder.DropIndex(
                name: "IX_UserArticles_UnreadByDate",
                table: "UserArticles");

            migrationBuilder.CreateIndex(
                name: "IX_UserArticles_UserId_IsRead_FeedId",
                table: "UserArticles",
                columns: new[] { "UserId", "IsRead", "FeedId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserArticles_UserId_IsSaved_SavedAt",
                table: "UserArticles",
                columns: new[] { "UserId", "IsSaved", "SavedAt" });
        }
    }
}
