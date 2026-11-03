using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reviewer.Engine.State.Migrations
{
    /// <inheritdoc />
    public partial class GateMerge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Merge",
                table: "Jobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Merge",
                table: "Jobs");
        }
    }
}
