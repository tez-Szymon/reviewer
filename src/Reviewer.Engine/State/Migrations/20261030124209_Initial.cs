using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reviewer.Engine.State.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Repo = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    HeadSha = table.Column<string>(type: "TEXT", nullable: true),
                    BaseSha = table.Column<string>(type: "TEXT", nullable: true),
                    ConfigHash = table.Column<string>(type: "TEXT", nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", nullable: false),
                    AppVersion = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    Published = table.Column<bool>(type: "INTEGER", nullable: false),
                    Decision = table.Column<string>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RetryAt = table.Column<long>(type: "INTEGER", nullable: true),
                    AttemptFolder = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PullRequests",
                columns: table => new
                {
                    Key = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Host = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Repo = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    LastReviewedHead = table.Column<string>(type: "TEXT", nullable: false),
                    LastReviewedBase = table.Column<string>(type: "TEXT", nullable: false),
                    LastReviewedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastJobId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PullRequests", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Findings",
                columns: table => new
                {
                    Key = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<string>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    FindingId = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    File = table.Column<string>(type: "TEXT", nullable: false),
                    StartLine = table.Column<int>(type: "INTEGER", nullable: false),
                    EndLine = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Suggestion = table.Column<string>(type: "TEXT", nullable: true),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    Disposition = table.Column<string>(type: "TEXT", nullable: false),
                    Blocking = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Findings", x => x.Key);
                    table.ForeignKey(
                        name: "FK_Findings_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Findings_Fingerprint",
                table: "Findings",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_Findings_JobId",
                table: "Findings",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CreatedAt",
                table: "Jobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Host_Repo_Number",
                table: "Jobs",
                columns: new[] { "Host", "Repo", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_PullRequests_Host_Repo_Number",
                table: "PullRequests",
                columns: new[] { "Host", "Repo", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Findings");

            migrationBuilder.DropTable(
                name: "PullRequests");

            migrationBuilder.DropTable(
                name: "Jobs");
        }
    }
}
