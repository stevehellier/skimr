using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Skimr.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddBoardsIconsSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "IconCheckedAt",
                table: "Feeds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "IconId",
                table: "Feeds",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                table: "Articles",
                type: "TEXT",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Boards",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Boards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Boards_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Icons",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Icons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BoardArticles",
                columns: table => new
                {
                    BoardId = table.Column<long>(type: "INTEGER", nullable: false),
                    ArticleId = table.Column<long>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardArticles", x => new { x.BoardId, x.ArticleId });
                    table.ForeignKey(
                        name: "FK_BoardArticles_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BoardArticles_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Feeds_IconId",
                table: "Feeds",
                column: "IconId");

            migrationBuilder.CreateIndex(
                name: "IX_BoardArticles_ArticleId",
                table: "BoardArticles",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_Boards_UserId_Name",
                table: "Boards",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Icons_Hash",
                table: "Icons",
                column: "Hash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Feeds_Icons_IconId",
                table: "Feeds",
                column: "IconId",
                principalTable: "Icons",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Search (PLAN D51). Existing articles only have a snippet of plain text; new and edited
            // ones get the full text. Kept in sync by triggers, so ingest code never touches it.
            // Any later migration that rebuilds the Articles table must recreate these triggers.
            migrationBuilder.Sql("UPDATE Articles SET SearchText = Snippet WHERE SearchText IS NULL;");
            migrationBuilder.Sql("CREATE VIRTUAL TABLE ArticleSearch USING fts5(Title, SearchText, content='Articles', content_rowid='Id', tokenize='unicode61 remove_diacritics 2');");
            migrationBuilder.Sql("CREATE TRIGGER ArticleSearch_Insert AFTER INSERT ON Articles BEGIN INSERT INTO ArticleSearch(rowid, Title, SearchText) VALUES (new.Id, new.Title, new.SearchText); END;");
            migrationBuilder.Sql("CREATE TRIGGER ArticleSearch_Delete AFTER DELETE ON Articles BEGIN INSERT INTO ArticleSearch(ArticleSearch, rowid, Title, SearchText) VALUES ('delete', old.Id, old.Title, old.SearchText); END;");
            migrationBuilder.Sql("CREATE TRIGGER ArticleSearch_Update AFTER UPDATE OF Title, SearchText ON Articles BEGIN INSERT INTO ArticleSearch(ArticleSearch, rowid, Title, SearchText) VALUES ('delete', old.Id, old.Title, old.SearchText); INSERT INTO ArticleSearch(rowid, Title, SearchText) VALUES (new.Id, new.Title, new.SearchText); END;");
            migrationBuilder.Sql("INSERT INTO ArticleSearch(ArticleSearch) VALUES ('rebuild');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS ArticleSearch_Insert; DROP TRIGGER IF EXISTS ArticleSearch_Delete; DROP TRIGGER IF EXISTS ArticleSearch_Update; DROP TABLE IF EXISTS ArticleSearch;");

            migrationBuilder.DropForeignKey(
                name: "FK_Feeds_Icons_IconId",
                table: "Feeds");

            migrationBuilder.DropTable(
                name: "BoardArticles");

            migrationBuilder.DropTable(
                name: "Icons");

            migrationBuilder.DropTable(
                name: "Boards");

            migrationBuilder.DropIndex(
                name: "IX_Feeds_IconId",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "IconCheckedAt",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "IconId",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "SearchText",
                table: "Articles");
        }
    }
}
