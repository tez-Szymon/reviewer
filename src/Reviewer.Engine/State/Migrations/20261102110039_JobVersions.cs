using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reviewer.Engine.State.Migrations
{
    /// <inheritdoc />
    public partial class JobVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Version",
                table: "Jobs",
                column: "Version");

            // Hand-written: number existing jobs in insertion order, then let the triggers take over.
            migrationBuilder.Sql("""UPDATE "Jobs" SET "Version" = rowid;""");
            migrationBuilder.Sql(JobVersionTriggers.CreateSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(JobVersionTriggers.DropSql);

            migrationBuilder.DropIndex(
                name: "IX_Jobs_Version",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Jobs");
        }
    }
}
