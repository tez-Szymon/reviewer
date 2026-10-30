using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reviewer.Engine.State.Migrations
{
    /// <inheritdoc />
    public partial class Queue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancelRequested",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Full",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "NotBefore",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RunnerPid",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_State",
                table: "Jobs",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_State",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "CancelRequested",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "Full",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "NotBefore",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RunnerPid",
                table: "Jobs");
        }
    }
}
