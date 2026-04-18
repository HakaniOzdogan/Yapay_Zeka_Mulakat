using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewCoach.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmRunMetadataTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LlmRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    PromptVersion = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: false),
                    InputHash = table.Column<string>(type: "text", nullable: false),
                    OutputJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LlmRuns_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LlmRuns_SessionId_Kind_CreatedAt",
                table: "LlmRuns",
                columns: new[] { "SessionId", "Kind", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_LlmRuns_SessionId_Kind_InputHash",
                table: "LlmRuns",
                columns: new[] { "SessionId", "Kind", "InputHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LlmRuns");
        }
    }
}
