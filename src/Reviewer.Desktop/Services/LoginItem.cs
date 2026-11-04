using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace Reviewer.Desktop.Services;

/// <summary>Starts the app when the user logs in, in the background: in the tray, with the window hidden.</summary>
internal interface ILoginItem
{
    bool IsEnabled { get; }

    /// <returns>Null when done, otherwise why not.</returns>
    string? Set(bool enabled);
}

internal static class LoginItem
{
    /// <summary>The argument the login item starts the app with: no window, just the tray and the engine.</summary>
    public const string Background = "--background";

    /// <summary>The app's bundle id, which also names its LaunchAgent.</summary>
    public const string BundleId = "com.github.tez-szymon.reviewer";

    /// <summary>The login item for this app on this OS: a LaunchAgent on macOS, a Run entry on Windows, none on Linux.</summary>
    public static ILoginItem? ForThisOs()
    {
        if (OperatingSystem.IsMacOS())
        {
            var agents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
            return new LaunchAgentLoginItem(Path.Combine(agents, BundleId + ".plist"), Environment.ProcessPath!);
        }

        if (OperatingSystem.IsWindows())
        {
            return new RunKeyLoginItem("Reviewer", WindowsExecutable());
        }

        return null;
    }

    /// <summary>
    /// Velopack's stub in the install folder, which starts the current version, so the entry outlives updates;
    /// the running executable when there's none (a development build).
    /// </summary>
    private static string WindowsExecutable()
    {
        var current = Environment.ProcessPath!;
        var root = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))?.FullName;
        var stub = root is null ? null : Path.Combine(root, Path.GetFileName(current));
        return stub is not null && File.Exists(stub) ? stub : current;
    }
}

/// <summary>
/// A LaunchAgent in <c>~/Library/LaunchAgents</c> that launchd runs at login. It isn't loaded when written, which would
/// start a second copy of the app at once. Inside an app bundle it starts the app with <c>open</c>, as Finder would.
/// </summary>
internal sealed class LaunchAgentLoginItem(string plistPath, string executable) : ILoginItem
{
    public bool IsEnabled => File.Exists(plistPath);

    public string? Set(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                File.Delete(plistPath);
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);
            var temporary = plistPath + ".tmp";
            File.WriteAllText(temporary, Plist());
            File.Move(temporary, plistPath, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't {(enabled ? "add" : "remove")} the login item {plistPath}: {e.Message}";
        }
    }

    /// <summary>The app bundle the executable is in, if it's in one.</summary>
    /// <remarks>
    /// A macOS path, split on <c>/</c> itself: <see cref="Path"/> would use the separators of the OS it runs on.
    /// </remarks>
    internal static string? Bundle(string executable)
    {
        // <bundle>.app/Contents/MacOS/<executable>
        var parts = executable.Split('/');
        return parts.Length >= 4
            && parts[^2] == "MacOS"
            && parts[^3] == "Contents"
            && parts[^4].EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            && parts[^4].Length > ".app".Length
                ? string.Join('/', parts[..^3])
                : null;
    }

    internal string Plist()
    {
        string[] arguments = Bundle(executable) is { } bundle
            ? ["/usr/bin/open", "-a", bundle, "--args", LoginItem.Background]
            : [executable, LoginItem.Background];
        var items = string.Concat(arguments.Select(a => $"\n    <string>{SecurityElement.Escape(a)}</string>"));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{LoginItem.BundleId}</string>
              <key>ProgramArguments</key>
              <array>{items}
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>LimitLoadToSessionType</key>
              <string>Aqua</string>
              <key>ProcessType</key>
              <string>Interactive</string>
            </dict>
            </plist>

            """;
    }
}

/// <summary>A value under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, which Windows runs at login.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RunKeyLoginItem(string name, string executable) : ILoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string Command => $"\"{executable}\" {LoginItem.Background}";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(name) is string;
        }
    }

    public string? Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                key.SetValue(name, Command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return $"Couldn't {(enabled ? "add" : "remove")} the login item: {e.Message}";
        }
    }
}
