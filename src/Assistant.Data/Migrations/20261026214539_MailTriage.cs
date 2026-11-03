using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Data.Migrations
{
    /// <inheritdoc />
    public partial class MailTriage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorAddress",
                table: "Signals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilteredReason",
                table: "Signals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FromMe",
                table: "Signals",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Signals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Recipients",
                table: "Signals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsReview",
                table: "Items",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ConnectorStates",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorStates", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ItemFeedback",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Useful = table.Column<bool>(type: "INTEGER", nullable: false),
                    ItemTitle = table.Column<string>(type: "TEXT", nullable: false),
                    Requester = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemFeedback", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TriageDecisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    DueAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DueConfidence = table.Column<double>(type: "REAL", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Requester = table.Column<string>(type: "TEXT", nullable: true),
                    Trust = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Reasoning = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    SourceContent = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TriagedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriageDecisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Signals_Source_ThreadKey",
                table: "Signals",
                columns: new[] { "Source", "ThreadKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemFeedback_CreatedAt",
                table: "ItemFeedback",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TriageDecisions_Source_Key",
                table: "TriageDecisions",
                columns: new[] { "Source", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectorStates");

            migrationBuilder.DropTable(
                name: "ItemFeedback");

            migrationBuilder.DropTable(
                name: "TriageDecisions");

            migrationBuilder.DropIndex(
                name: "IX_Signals_Source_ThreadKey",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "AuthorAddress",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "FilteredReason",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "FromMe",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "Recipients",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "NeedsReview",
                table: "Items");
        }
    }
}
