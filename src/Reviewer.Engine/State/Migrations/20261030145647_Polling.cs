using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reviewer.Engine.State.Migrations
{
    /// <inheritdoc />
    public partial class Polling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PollStates",
                columns: table => new
                {
                    Key = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Host = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Repo = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    ETag = table.Column<string>(type: "TEXT", nullable: true),
                    RulesHash = table.Column<string>(type: "TEXT", nullable: false),
                    LastPolledAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PollStates", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SeenPullRequests",
                columns: table => new
                {
                    Key = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Host = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Repo = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    HeadSha = table.Column<string>(type: "TEXT", nullable: false),
                    Matched = table.Column<bool>(type: "INTEGER", nullable: false),
                    Assigned = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReviewRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    SeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeenPullRequests", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PollStates_Host_Repo",
                table: "PollStates",
                columns: new[] { "Host", "Repo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeenPullRequests_Host_Repo_Number",
                table: "SeenPullRequests",
                columns: new[] { "Host", "Repo", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PollStates");

            migrationBuilder.DropTable(
                name: "SeenPullRequests");
        }
    }
}
