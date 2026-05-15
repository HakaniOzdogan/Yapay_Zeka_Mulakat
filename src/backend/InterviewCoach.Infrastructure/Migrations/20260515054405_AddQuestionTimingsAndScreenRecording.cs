using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewCoach.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionTimingsAndScreenRecording : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // QuestionOrder: TranscriptSegments tablosuna yeni sütun
            migrationBuilder.AddColumn<int>(
                name: "QuestionOrder",
                table: "TranscriptSegments",
                type: "integer",
                nullable: true);

            // Questions tablosuna yeni sütunlar (AudioUrl zaten mevcut — atlanıyor)
            migrationBuilder.AddColumn<long>(
                name: "EndMs",
                table: "Questions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScreenAudioUrl",
                table: "Questions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StartMs",
                table: "Questions",
                type: "bigint",
                nullable: true);

            // BatchCoachingJobs ve BatchCoachingJobItems veritabanında zaten mevcut — atlanıyor
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuestionOrder",
                table: "TranscriptSegments");

            migrationBuilder.DropColumn(
                name: "EndMs",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "ScreenAudioUrl",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "StartMs",
                table: "Questions");
        }
    }
}
