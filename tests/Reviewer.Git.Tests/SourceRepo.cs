using System.Diagnostics;

namespace Reviewer.Git.Tests;

/// <summary>
/// A throwaway working repository playing the role of the remote. Git runs with an empty global config, so the
/// developer's own settings (signing, hooks, default branch) can't affect the tests.
/// </summary>
internal sealed class SourceRepo : IDisposable
{
    public SourceRepo()
    {
        Root = Directory.CreateTempSubdirectory("reviewer-git-tests-").FullName;
        Path = Directory.CreateDirectory(System.IO.Path.Combine(Root, "source")).FullName;
        GlobalConfig = System.IO.Path.Combine(Root, "global.gitconfig");
        File.WriteAllText(GlobalConfig, "");

        // GitRepository inherits these from the test process. Tests in one class run sequentially.
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", GlobalConfig);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

        Git("init", "--quiet", "--initial-branch=main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.com");
    }

    public string Root { get; }

    public string Path { get; }

    public string GlobalConfig { get; }

    public string Url => new Uri(Path).AbsoluteUri;

    public string NewDirectory(string name) => System.IO.Path.Combine(Root, name);

    public void Write(string file, params string[] lines)
    {
        var full = System.IO.Path.Combine(Path, file);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Concat(lines.Select(l => l + "\n")));
    }

    public string Commit(string message)
    {
        Git("add", "--all");
        Git("commit", "--quiet", "--allow-empty", "-m", message);
        return Head();
    }

    public string Head() => Git("rev-parse", "HEAD").Trim();

    public string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? stdout
            : throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering temp folder must not fail the test.
        }
        catch (UnauthorizedAccessException)
        {
            // Git marks object files read-only on Windows.
        }
    }
}
