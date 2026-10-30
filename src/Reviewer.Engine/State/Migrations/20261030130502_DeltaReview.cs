using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reviewer.Engine.State.Migrations
{
    /// <inheritdoc />
    public partial class DeltaReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReviewedCommitPatchIds",
                table: "PullRequests",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ReviewedFilePatchIds",
                table: "PullRequests",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}"); // Edited: rows from before this migration hold no per-file ids, and "" isn't JSON.

            migrationBuilder.AddColumn<string>(
                name: "Delta",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeltaFrom",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedByJobId",
                table: "Findings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewedCommitPatchIds",
                table: "PullRequests");

            migrationBuilder.DropColumn(
                name: "ReviewedFilePatchIds",
                table: "PullRequests");

            migrationBuilder.DropColumn(
                name: "Delta",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "DeltaFrom",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ResolvedByJobId",
                table: "Findings");
        }
    }
}
