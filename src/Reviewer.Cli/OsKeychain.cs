using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Reviewer.Agents.Cli;
using Reviewer.Engine.Config;

namespace Reviewer.Cli;

/// <summary>The keychain couldn't be read: locked, access denied, or its tool missing. Not raised for a missing item.</summary>
internal sealed class KeychainException(string message) : InvalidOperationException(message);

internal interface IKeychain
{
    /// <summary>The item's secret, or null when there is no such item.</summary>
    /// <exception cref="KeychainException">The keychain couldn't be read.</exception>
    Task<string?> ReadAsync(TokenReference.Keychain item, CancellationToken ct);
}

/// <summary>
/// Reads generic passwords from the OS credential store: the macOS Keychain through <c>security</c>, libsecret through
/// <c>secret-tool</c>, and Windows Credential Manager through <c>CredReadW</c>. Read-only: tokens are added with the
/// platform's own tools (see <see cref="AddCommand"/>), so they never pass through this process's arguments or logs.
/// </summary>
internal sealed partial class OsKeychain(IProcessRunner processes) : IKeychain
{
    // security(1) exits with errSecItemNotFound's low byte when there's no such item; secret-tool exits 1 with no output.
    private const int MacNotFound = 44;

    public async Task<string?> ReadAsync(TokenReference.Keychain item, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            return ReadWindows(item.Target);
        }

        var (tool, arguments) = OperatingSystem.IsMacOS()
            ? ("security", (string[])["find-generic-password", "-s", item.Service, "-a", item.Account, "-w"])
            : ("secret-tool", ["lookup", "service", item.Service, "account", item.Account]);
        ProcessResult result;
        try
        {
            result = await processes.RunAsync(
                new ProcessSpec(tool, arguments, Environment.CurrentDirectory, new Dictionary<string, string?>()), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception || e.InnerException is Win32Exception)
        {
            throw new KeychainException($"Can't read {item.Describe()}: {tool} is not installed.");
        }

        // Only the trailing newline the tools add is trimmed; a token never ends in whitespace anyway.
        var secret = result.StandardOutput.TrimEnd('\r', '\n');
        return result.ExitCode switch
        {
            0 when secret.Length > 0 => secret,
            0 => null,
            MacNotFound when OperatingSystem.IsMacOS() => null,
            1 when !OperatingSystem.IsMacOS() && result.StandardError.Trim().Length == 0 => null,
            _ => throw new KeychainException(
                $"Can't read {item.Describe()}: {tool} exited with {result.ExitCode}. {result.StandardError.Trim()}".TrimEnd()),
        };
    }

    /// <summary>The command that adds the item on this OS; each one prompts for the token rather than taking it as an argument.</summary>
    public static string AddCommand(TokenReference.Keychain item) =>
        OperatingSystem.IsWindows() ? $"cmdkey /generic:{item.Target} /user:{item.Account} /pass"
        : OperatingSystem.IsMacOS() ? $"security add-generic-password -s {item.Service} -a {item.Account} -U -w"
        : $"secret-tool store --label={item.Target} service {item.Service} account {item.Account}";

    private static string? ReadWindows(string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (!CredRead(target, CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound
                ? null
                : throw new KeychainException($"Can't read keychain item {target}: {new Win32Exception(error).Message}");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);

            // cmdkey and the Credential Manager UI store UTF-16; other tools often store UTF-8, which has an odd length or no NULs.
            return blob.Length % 2 == 0 && blob.Skip(1).Where((_, i) => i % 2 == 0).All(b => b == 0)
                ? Encoding.Unicode.GetString(blob)
                : Encoding.UTF8.GetString(blob);
        }
        finally
        {
            CredFree(handle);
        }
    }

    private const int CredTypeGeneric = 1;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public int LastWrittenLow;
        public int LastWrittenHigh;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
