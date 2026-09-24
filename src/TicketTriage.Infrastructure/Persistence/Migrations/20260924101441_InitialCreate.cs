using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketTriage.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrainingTickets",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    WorkType = table.Column<string>(type: "TEXT", nullable: true),
                    AffectedServices = table.Column<string>(type: "TEXT", nullable: false),
                    ServiceTeams = table.Column<string>(type: "TEXT", nullable: false),
                    Assignee = table.Column<string>(type: "TEXT", nullable: true),
                    Urgency = table.Column<string>(type: "TEXT", nullable: true),
                    Impact = table.Column<string>(type: "TEXT", nullable: true),
                    Priority = table.Column<string>(type: "TEXT", nullable: true),
                    Resolution = table.Column<string>(type: "TEXT", nullable: true),
                    Created = table.Column<long>(type: "INTEGER", nullable: true),
                    Comments = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingTickets", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "TriageSuggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TicketKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SuggestionJson = table.Column<string>(type: "TEXT", nullable: false),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EditedJson = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewerComment = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReviewedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriageSuggestions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrainingTickets_Assignee",
                table: "TrainingTickets",
                column: "Assignee");

            migrationBuilder.CreateIndex(
                name: "IX_TrainingTickets_WorkType",
                table: "TrainingTickets",
                column: "WorkType");

            migrationBuilder.CreateIndex(
                name: "IX_TriageSuggestions_Decision",
                table: "TriageSuggestions",
                column: "Decision");

            migrationBuilder.CreateIndex(
                name: "IX_TriageSuggestions_TicketKey",
                table: "TriageSuggestions",
                column: "TicketKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrainingTickets");

            migrationBuilder.DropTable(
                name: "TriageSuggestions");
        }
    }
}
