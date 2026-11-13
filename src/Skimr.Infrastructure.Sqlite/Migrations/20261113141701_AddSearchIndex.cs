using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Skimr.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        }
    }
}
