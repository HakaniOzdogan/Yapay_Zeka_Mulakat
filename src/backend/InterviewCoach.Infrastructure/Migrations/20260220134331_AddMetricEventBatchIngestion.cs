using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewCoach.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMetricEventBatchIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetricEvents_SessionId",
                table: "MetricEvents");

            migrationBuilder.RenameColumn(
                name: "TimestampMs",
                table: "MetricEvents",
                newName: "TsMs");

            migrationBuilder.RenameColumn(
                name: "ValueJson",
                table: "MetricEvents",
                newName: "PayloadJson");

            migrationBuilder.Sql("""
UPDATE "MetricEvents"
SET "PayloadJson" = '{}'
WHERE "PayloadJson" IS NULL OR "PayloadJson" = '';
""");

            migrationBuilder.Sql(@"ALTER TABLE ""MetricEvents"" ALTER COLUMN ""PayloadJson"" TYPE jsonb USING ""PayloadJson""::jsonb;");

            migrationBuilder.AddColumn<Guid>(
                name: "ClientEventId",
                table: "MetricEvents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "MetricEvents",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "MetricEvents",
                type: "text",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.CreateIndex(
                name: "IX_MetricEvents_SessionId_TsMs",
                table: "MetricEvents",
                columns: new[] { "SessionId", "TsMs" });

            migrationBuilder.CreateIndex(
                name: "UX_MetricEvents_SessionId_ClientEventId",
                table: "MetricEvents",
                columns: new[] { "SessionId", "ClientEventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetricEvents_SessionId_TsMs",
                table: "MetricEvents");

            migrationBuilder.DropIndex(
                name: "UX_MetricEvents_SessionId_ClientEventId",
                table: "MetricEvents");

            migrationBuilder.DropColumn(
                name: "ClientEventId",
                table: "MetricEvents");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "MetricEvents");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "MetricEvents");

            migrationBuilder.RenameColumn(
                name: "TsMs",
                table: "MetricEvents",
                newName: "TimestampMs");

            migrationBuilder.RenameColumn(
                name: "PayloadJson",
                table: "MetricEvents",
                newName: "ValueJson");

            migrationBuilder.AlterColumn<string>(
                name: "ValueJson",
                table: "MetricEvents",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.CreateIndex(
                name: "IX_MetricEvents_SessionId",
                table: "MetricEvents",
                column: "SessionId");
        }
    }
}
