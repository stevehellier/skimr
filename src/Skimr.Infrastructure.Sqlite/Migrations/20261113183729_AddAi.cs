using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Skimr.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AiEnabled",
                table: "UserPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AiFeeds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Rule = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastCheckedArticleId = table.Column<long>(type: "INTEGER", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiFeeds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiFeeds_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiUsage",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Day = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Calls = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsage", x => new { x.UserId, x.Day });
                    table.ForeignKey(
                        name: "FK_AiUsage_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArticleSummaries",
                columns: table => new
                {
                    ArticleId = table.Column<long>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleSummaries", x => new { x.ArticleId, x.Model });
                    table.ForeignKey(
                        name: "FK_ArticleSummaries_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiFeedMatches",
                columns: table => new
                {
                    AiFeedId = table.Column<long>(type: "INTEGER", nullable: false),
                    ArticleId = table.Column<long>(type: "INTEGER", nullable: false),
                    MatchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiFeedMatches", x => new { x.AiFeedId, x.ArticleId });
                    table.ForeignKey(
                        name: "FK_AiFeedMatches_AiFeeds_AiFeedId",
                        column: x => x.AiFeedId,
                        principalTable: "AiFeeds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiFeedMatches_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiFeedSources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AiFeedId = table.Column<long>(type: "INTEGER", nullable: false),
                    FolderId = table.Column<long>(type: "INTEGER", nullable: true),
                    SubscriptionId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiFeedSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiFeedSources_AiFeeds_AiFeedId",
                        column: x => x.AiFeedId,
                        principalTable: "AiFeeds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiFeedSources_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiFeedSources_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiFeedMatches_ArticleId",
                table: "AiFeedMatches",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_AiFeeds_UserId",
                table: "AiFeeds",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AiFeedSources_AiFeedId",
                table: "AiFeedSources",
                column: "AiFeedId");

            migrationBuilder.CreateIndex(
                name: "IX_AiFeedSources_FolderId",
                table: "AiFeedSources",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_AiFeedSources_SubscriptionId",
                table: "AiFeedSources",
                column: "SubscriptionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiFeedMatches");

            migrationBuilder.DropTable(
                name: "AiFeedSources");

            migrationBuilder.DropTable(
                name: "AiUsage");

            migrationBuilder.DropTable(
                name: "ArticleSummaries");

            migrationBuilder.DropTable(
                name: "AiFeeds");

            migrationBuilder.DropColumn(
                name: "AiEnabled",
                table: "UserPreferences");
        }
    }
}
