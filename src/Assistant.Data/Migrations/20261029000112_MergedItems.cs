using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Data.Migrations
{
    /// <inheritdoc />
    public partial class MergedItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MergedIntoId",
                table: "Items",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_MergedIntoId",
                table: "Items",
                column: "MergedIntoId");

            migrationBuilder.AddForeignKey(
                name: "FK_Items_Items_MergedIntoId",
                table: "Items",
                column: "MergedIntoId",
                principalTable: "Items",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Items_Items_MergedIntoId",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Items_MergedIntoId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "MergedIntoId",
                table: "Items");
        }
    }
}
