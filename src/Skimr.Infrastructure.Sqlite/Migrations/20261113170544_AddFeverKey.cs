using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Skimr.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddFeverKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FeverKeyHash",
                table: "ApiTokens",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_FeverKeyHash",
                table: "ApiTokens",
                column: "FeverKeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApiTokens_FeverKeyHash",
                table: "ApiTokens");

            migrationBuilder.DropColumn(
                name: "FeverKeyHash",
                table: "ApiTokens");
        }
    }
}
