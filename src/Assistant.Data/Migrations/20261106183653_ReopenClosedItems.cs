using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReopenClosedItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReopenedAt",
                table: "TriageDecisions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReopenedAt",
                table: "TriageDecisions");
        }
    }
}
