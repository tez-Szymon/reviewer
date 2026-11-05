using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Reviewer.Agents.Cli;
using Reviewer.Engine.Config;

namespace Reviewer.Cli;

/// <summary>
/// The keychain couldn't be read or written: locked, access denied, or its tool missing. Not raised for a missing item.
/// </summary>
internal sealed class KeychainException(string message) : InvalidOperationException(message);

internal interface IKeychain
{
    /// <summary>The item's secret, or null when there is no such item.</summary>
    /// <exception cref="KeychainException">The keychain couldn't be read.</exception>
    Task<string?> ReadAsync(TokenReference.Keychain item, CancellationToken ct);

    /// <summary>Adds the item, or replaces its secret when there is one.</summary>
    /// <exception cref="KeychainException">The keychain couldn't be written, or the secret doesn't look like a token.</exception>
    Task WriteAsync(TokenReference.Keychain item, string secret, CancellationToken ct);
}

/// <summary>
/// Reads and writes generic passwords in the OS credential store: the macOS Keychain through <c>security</c>, libsecret
/// through <c>secret-tool</c>, and Windows Credential Manager through <c>CredReadW</c> and <c>CredWriteW</c>. A secret
/// goes to a tool on its standard input, never in its arguments, which any process can list.
/// </summary>
/// <remarks>
/// On macOS, writing with <c>security</c> too matters: the item's access list then names <c>security</c>, which reads it
/// back without a prompt.
/// </remarks>
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
        var result = await RunAsync("read", item, tool, arguments, null, ct).ConfigureAwait(false);

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

    public async Task WriteAsync(TokenReference.Keychain item, string secret, CancellationToken ct)
    {
        // Tokens are printable ASCII without spaces or quotes. Anything else is a paste gone wrong, and security -i
        // would need it escaped; the item's names are held to the same, as they're quoted the same way.
        if (!Plain(secret))
        {
            throw new KeychainException("That doesn't look like a token: tokens have no spaces, quotes or line breaks.");
        }

        if (!Plain(item.Service) || !Plain(item.Account))
        {
            throw new KeychainException($"Can't save {item.Describe()}: its name has spaces or quotes.");
        }

        if (OperatingSystem.IsWindows())
        {
            WriteWindows(item, secret);
            return;
        }

        // security -i reads its command from standard input, so the token stays out of the process list; -U replaces
        // an existing item's secret. secret-tool store reads the secret from standard input itself.
        var result = OperatingSystem.IsMacOS()
            ? await RunAsync("save", item, "security", ["-i"],
                $"add-generic-password -U -s \"{item.Service}\" -a \"{item.Account}\" -w \"{secret}\"\n", ct).ConfigureAwait(false)
            : await RunAsync("save", item, "secret-tool", ["store", $"--label={item.Target}", "service", item.Service, "account", item.Account],
                secret, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new KeychainException($"Can't save {item.Describe()}: exited with {result.ExitCode}. {result.StandardError.Trim()}".TrimEnd());
        }

        // security -i reports a failed command on its output and still exits 0, so read the item back to be sure.
        if (await ReadAsync(item, ct).ConfigureAwait(false) != secret)
        {
            throw new KeychainException($"Can't save {item.Describe()}: it doesn't read back as saved. {result.StandardError.Trim()}".TrimEnd());
        }
    }

    /// <summary>The command that adds the item on this OS; each one prompts for the token rather than taking it as an argument.</summary>
    public static string AddCommand(TokenReference.Keychain item) =>
        OperatingSystem.IsWindows() ? $"cmdkey /generic:{item.Target} /user:{item.Account} /pass"
        : OperatingSystem.IsMacOS() ? $"security add-generic-password -s {item.Service} -a {item.Account} -U -w"
        : $"secret-tool store --label={item.Target} service {item.Service} account {item.Account}";

    private async Task<ProcessResult> RunAsync(
        string verb, TokenReference.Keychain item, string tool, IReadOnlyList<string> arguments, string? input, CancellationToken ct)
    {
        try
        {
            return await processes.RunAsync(
                new ProcessSpec(tool, arguments, Environment.CurrentDirectory, new Dictionary<string, string?>(), input), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception || e.InnerException is Win32Exception)
        {
            throw new KeychainException($"Can't {verb} {item.Describe()}: {tool} is not installed.");
        }
    }

    private static bool Plain(string value) => value.Length > 0 && value.All(c => c is > ' ' and <= '~' and not '"' and not '\\');

    private static void WriteWindows(TokenReference.Keychain item, string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        // UTF-16, as cmdkey and the Credential Manager UI store it.
        var target = Marshal.StringToCoTaskMemUni(item.Target);
        var user = Marshal.StringToCoTaskMemUni(item.Account);
        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = user,
                CredentialBlob = blob,
                CredentialBlobSize = secret.Length * 2,
                Persist = CredPersistLocalMachine,
            };
            if (!CredWrite(credential, 0))
            {
                throw new KeychainException($"Can't save keychain item {item.Target}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

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

    // Kept across logons on this machine, as cmdkey does.
    private const int CredPersistLocalMachine = 2;

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

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(in Credential credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
