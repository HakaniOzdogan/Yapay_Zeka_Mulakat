using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewCoach.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscriptBatchIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TranscriptSegments_SessionId",
                table: "TranscriptSegments");

            migrationBuilder.AddColumn<Guid>(
                name: "ClientSegmentId",
                table: "TranscriptSegments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.Sql("""
UPDATE "TranscriptSegments"
SET "ClientSegmentId" = "Id"
WHERE "ClientSegmentId" = '00000000-0000-0000-0000-000000000000';
""");

            migrationBuilder.AddColumn<double>(
                name: "Confidence",
                table: "TranscriptSegments",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "TranscriptSegments",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.CreateIndex(
                name: "IX_TranscriptSegments_SessionId_StartMs",
                table: "TranscriptSegments",
                columns: new[] { "SessionId", "StartMs" });

            migrationBuilder.CreateIndex(
                name: "UX_TranscriptSegments_SessionId_ClientSegmentId",
                table: "TranscriptSegments",
                columns: new[] { "SessionId", "ClientSegmentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TranscriptSegments_SessionId_StartMs",
                table: "TranscriptSegments");

            migrationBuilder.DropIndex(
                name: "UX_TranscriptSegments_SessionId_ClientSegmentId",
                table: "TranscriptSegments");

            migrationBuilder.DropColumn(
                name: "ClientSegmentId",
                table: "TranscriptSegments");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "TranscriptSegments");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "TranscriptSegments");

            migrationBuilder.CreateIndex(
                name: "IX_TranscriptSegments_SessionId",
                table: "TranscriptSegments",
                column: "SessionId");
        }
    }
}
