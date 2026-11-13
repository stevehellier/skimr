using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Skimr.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Search (PLAN D51): an expression index; SearchService queries the identical expression
            // so the planner can use it. 'simple' = no stemming, because feeds come in any language.
            migrationBuilder.Sql("UPDATE \"Articles\" SET \"SearchText\" = \"Snippet\" WHERE \"SearchText\" IS NULL;");
            migrationBuilder.Sql("CREATE INDEX \"IX_Articles_Search\" ON \"Articles\" USING GIN (to_tsvector('simple'::regconfig, \"Title\" || ' ' || COALESCE(\"SearchText\", '')));");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Articles_Search\";");

        }
    }
}
