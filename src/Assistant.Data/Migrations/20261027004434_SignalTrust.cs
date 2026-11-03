using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Data.Migrations
{
    /// <inheritdoc />
    public partial class SignalTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorTrust",
                table: "Signals",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthorTrust",
                table: "Signals");
        }
    }
}
