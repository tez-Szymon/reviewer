using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Reviewer.Core.Findings;
using Reviewer.Core.Providers;

namespace Reviewer.Engine.State;

public enum JobState
{
    Queued,
    Running,

    /// <summary>The agent hit a usage or rate limit; the job can run again after <see cref="JobRecord.RetryAt"/>.</summary>
    Throttled,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>One review of one PR head: a row per <c>jobs/&lt;id&gt;</c> folder.</summary>
public sealed class JobRecord
{
    /// <summary>Also the job folder's name.</summary>
    public required string Id { get; set; }

    /// <summary>Scheme and host, e.g. <c>https://github.com</c>.</summary>
    public required string Host { get; set; }

    /// <summary><c>owner/name</c>.</summary>
    public required string Repo { get; set; }

    public int Number { get; set; }

    /// <summary>Null until the PR's snapshot has been read.</summary>
    public string? HeadSha { get; set; }

    public string? BaseSha { get; set; }

    public required string ConfigHash { get; set; }

    public required string AgentId { get; set; }

    /// <summary>The engine version that ran the review; the built-in instructions are versioned with it.</summary>
    public required string AppVersion { get; set; }

    public JobState State { get; set; }

    public bool DryRun { get; set; }

    /// <summary>The plan reached the PR. False for dry runs and jobs that didn't finish.</summary>
    public bool Published { get; set; }

    public ReviewDecision? Decision { get; set; }

    /// <summary>The agent's summary, for history.</summary>
    public string? Summary { get; set; }

    /// <summary>Why the job failed, was throttled or cancelled.</summary>
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public DateTimeOffset? RetryAt { get; set; }

    /// <summary>The latest attempt's folder, with the agent's inputs, output and <c>plan.json</c>.</summary>
    public string? AttemptFolder { get; set; }

    public ICollection<FindingRecord> Findings { get; } = new List<FindingRecord>();
}

/// <summary>What the policy engine did with a finding.</summary>
public enum FindingDisposition
{
    Inline,

    /// <summary>Listed in the summary only: outside the delta or over the inline limit.</summary>
    Summary,

    /// <summary>Filtered out: below the confidence threshold, in an ignored path, or not in the delta's files.</summary>
    Dropped,
}

/// <summary>A finding as the agent reported it, with what the policy engine did with it.</summary>
public sealed class FindingRecord
{
    public long Key { get; set; }

    public required string JobId { get; set; }

    /// <summary>Position in the agent's report, so findings list in the order the agent gave them.</summary>
    public int Ordinal { get; set; }

    /// <summary>The agent's id for the finding; unique within its report only.</summary>
    public required string FindingId { get; set; }

    public Severity Severity { get; set; }

    public Category Category { get; set; }

    public double Confidence { get; set; }

    public required string File { get; set; }

    public int StartLine { get; set; }

    public int EndLine { get; set; }

    public required string Title { get; set; }

    public required string Body { get; set; }

    public string? Suggestion { get; set; }

    /// <summary>Set for findings the plan kept; dropped ones never reach the PR, so they need none.</summary>
    public string? Fingerprint { get; set; }

    public FindingDisposition Disposition { get; set; }

    public bool Blocking { get; set; }

    public Finding ToFinding() => new(FindingId, Severity, Category, Confidence, File, StartLine, EndLine, Title, Body, Suggestion);
}

/// <summary>A PR's last published review: the starting point for the next one.</summary>
public sealed class PullRequestRecord
{
    public long Key { get; set; }

    public required string Host { get; set; }

    public required string Repo { get; set; }

    public int Number { get; set; }

    public required string LastReviewedHead { get; set; }

    public required string LastReviewedBase { get; set; }

    public DateTimeOffset LastReviewedAt { get; set; }

    public required string LastJobId { get; set; }
}

/// <summary>The engine's SQLite database, <c>reviewer.db</c> in the data folder.</summary>
public sealed class ReviewerDb(DbContextOptions<ReviewerDb> options) : DbContext(options)
{
    public DbSet<JobRecord> Jobs => Set<JobRecord>();

    public DbSet<FindingRecord> Findings => Set<FindingRecord>();

    public DbSet<PullRequestRecord> PullRequests => Set<PullRequestRecord>();

    /// <remarks>
    /// No connection pooling: an idle pooled connection keeps the file open, which on Windows blocks deleting or moving
    /// the data folder. Opening a local SQLite file is cheap enough without it.
    /// </remarks>
    public static DbContextOptions<ReviewerDb> Options(string path) =>
        new DbContextOptionsBuilder<ReviewerDb>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString())
            .Options;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums as their names, so the database reads on its own. Times as UTC milliseconds: SQLite can't order
        // DateTimeOffset text, and the offset isn't worth keeping.
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobRecord>(job =>
        {
            job.ToTable("Jobs");
            // Hosts and repo names are case-insensitive on both providers, so one PR has one history.
            job.Property(j => j.Host).UseCollation("NOCASE");
            job.Property(j => j.Repo).UseCollation("NOCASE");
            job.HasIndex(j => new { j.Host, j.Repo, j.Number });
            job.HasIndex(j => j.CreatedAt);
            job.HasMany(j => j.Findings).WithOne().HasForeignKey(f => f.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FindingRecord>(finding =>
        {
            finding.ToTable("Findings");
            finding.HasKey(f => f.Key);
            finding.HasIndex(f => f.Fingerprint);
        });

        modelBuilder.Entity<PullRequestRecord>(pr =>
        {
            pr.ToTable("PullRequests");
            pr.Property(p => p.Host).UseCollation("NOCASE");
            pr.Property(p => p.Repo).UseCollation("NOCASE");
            pr.HasKey(p => p.Key);
            pr.HasIndex(p => new { p.Host, p.Repo, p.Number }).IsUnique();
        });
    }

    private sealed class UnixMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
        v => v.ToUnixTimeMilliseconds(),
        v => DateTimeOffset.FromUnixTimeMilliseconds(v));
}

/// <summary>Lets <c>dotnet ef migrations add</c> build the model without the engine running.</summary>
internal sealed class ReviewerDbDesignFactory : IDesignTimeDbContextFactory<ReviewerDb>
{
    public ReviewerDb CreateDbContext(string[] args) => new(ReviewerDb.Options("design.db"));
}
