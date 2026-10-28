using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DependencyInventory;

/// <summary>An operating system vault holding one set of credentials per key.</summary>
public interface ICredentialStore
{
    /// <summary>Name shown to the user, e.g. "Windows Credential Manager".</summary>
    string Name { get; }

    RepositoryCredentials? Read(string key);
    void Write(string key, RepositoryCredentials credentials);

    /// <summary>Returns false when no entry exists for the key.</summary>
    bool Delete(string key);

    /// <summary>
    /// Whether --credentials-file is still accepted next to this vault: on systems where the vault may be missing
    /// or locked, such as a Linux server without a Secret Service or a macOS SSH session, files remain the way to
    /// run unattended.
    /// </summary>
    bool AcceptsCredentialsFiles => false;
}

/// <summary>
/// The vault cannot be reached, e.g. no Secret Service runs, its tool is not installed or the macOS keychain is
/// locked.
/// </summary>
public sealed class CredentialStoreUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public static class CredentialStores
{
    /// <summary>The vault of the current system, or null where none is supported yet.</summary>
    public static ICredentialStore? ForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsCredentialStore()
        : OperatingSystem.IsLinux() ? new SecretServiceStore()
        : OperatingSystem.IsMacOS() ? new MacKeychainStore()
        : null;

    /// <summary>
    /// One entry per provider and origin: an Azure DevOps PAT belongs to an organization,
    /// a GitLab PAT to an instance.
    /// </summary>
    public static string KeyFor(SourceProvider provider, string url) => provider switch
    {
        SourceProvider.Azure => AzureKey(AzureOrigin.Parse(url).Organization),
        SourceProvider.GitLab =>
            $"dependency-inventory:gitlab:{new Uri(GitLabOrigin.Parse(url).BaseUrl).Authority.ToLowerInvariant()}",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    /// <summary>The entry of an Azure DevOps organization, the one its feeds are read with.</summary>
    public static string AzureKey(string organization) =>
        $"dependency-inventory:azure:dev.azure.com/{organization.ToLowerInvariant()}";
}

public enum CredentialSource { File, Store, Prompt }

/// <param name="StoreUnavailable">Why the vault could not be read, when the prompt replaced it.</param>
public sealed record ResolvedCredentials(RepositoryCredentials? Credentials, CredentialSource Source,
    string? StoreUnavailable = null);

public static class CredentialResolver
{
    /// <summary>
    /// Credentials file when requested, then the vault entry, then the prompt. Never writes to the vault:
    /// only the login command does. File errors are left to the caller.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// A credentials file is requested next to a vault that does not accept them (Windows): files are only
    /// accepted where a vault may be missing, until they are removed.
    /// </exception>
    public static Task<ResolvedCredentials> ResolveAsync(SourceProvider provider, string url,
        bool useFile, string? filePath, ICredentialStore? store, Func<RepositoryCredentials?> prompt,
        CancellationToken ct) =>
        ResolveKeyAsync(provider, () => CredentialStores.KeyFor(provider, url), useFile, filePath, store, prompt, ct);

    /// <summary>As <see cref="ResolveAsync"/>, for the vault entry <paramref name="key"/> gives.</summary>
    public static async Task<ResolvedCredentials> ResolveKeyAsync(SourceProvider provider, Func<string> key,
        bool useFile, string? filePath, ICredentialStore? store, Func<RepositoryCredentials?> prompt,
        CancellationToken ct)
    {
        if (useFile)
        {
            if (store is { AcceptsCredentialsFiles: false })
                throw new NotSupportedException(
                    $"--credentials-file is not available with the {store.Name}: store the credentials " +
                    "once with \"depinv login\".");
            var path = CredentialsFile.ResolvePath(provider, filePath);
            return new(await CredentialsFile.ReadAsync(path, provider, ct), CredentialSource.File);
        }

        try
        {
            if (store?.Read(key()) is { } stored)
                return new(stored, CredentialSource.Store);
        }
        catch (CredentialStoreUnavailableException ex)
        {
            return new(prompt(), CredentialSource.Prompt, ex.Message);
        }

        return new(prompt(), CredentialSource.Prompt);
    }
}

/// <summary>Generic credentials of the Windows Credential Manager, encrypted with the user's account.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint GenericType = 1;          // CRED_TYPE_GENERIC
    private const uint PersistLocalMachine = 2;  // CRED_PERSIST_LOCAL_MACHINE
    private const int NotFound = 1168;           // ERROR_NOT_FOUND
    private const string NoLogin = "-";          // Stored for GitLab, whose PAT has no login.

    public string Name => "Windows Credential Manager";

    public RepositoryCredentials? Read(string key)
    {
        if (!CredRead(key, GenericType, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            return error == NotFound ? null : throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            var secret = credential.CredentialBlobSize == 0
                ? ""
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            var login = Marshal.PtrToStringUni(credential.UserName) ?? "";
            return new RepositoryCredentials(login == NoLogin ? "" : login, secret);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public void Write(string key, RepositoryCredentials credentials)
    {
        var secret = Encoding.Unicode.GetBytes(credentials.Pat);
        var blob = Marshal.AllocHGlobal(secret.Length);
        var target = Marshal.StringToHGlobalUni(key);
        var userName = Marshal.StringToHGlobalUni(credentials.Login.Length == 0 ? NoLogin : credentials.Login);
        try
        {
            Marshal.Copy(secret, 0, blob, secret.Length);
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = target,
                CredentialBlobSize = (uint)secret.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = userName
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            // The secret is cleared from unmanaged memory before it is released.
            for (var i = 0; i < secret.Length; i++) Marshal.WriteByte(blob, i, 0);
            Array.Clear(secret);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(userName);
        }
    }

    public bool Delete(string key)
    {
        if (CredDelete(key, GenericType, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        return error == NotFound ? false : throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>
/// The Secret Service of Linux (GNOME Keyring, KWallet), through its secret-tool command. Each entry has the key
/// and the login as attributes, the PAT as secret. The PAT is passed on standard input, never on a command line,
/// which other users can read.
/// </summary>
public sealed class SecretServiceStore : ICredentialStore
{
    private const string KeyAttribute = "key";
    private const string LoginAttribute = "login";

    /// <summary>Runs secret-tool with the arguments and the standard input given.</summary>
    public delegate SecretToolResult Runner(IReadOnlyList<string> arguments, string? input);

    private readonly Runner _run;

    public SecretServiceStore() : this(RunSecretTool) { }

    public SecretServiceStore(Runner run) => _run = run;

    public string Name => "Secret Service";

    public bool AcceptsCredentialsFiles => true;

    public RepositoryCredentials? Read(string key)
    {
        // search writes the secret on standard output and the attributes on standard error; an empty output
        // means no entry.
        var result = Run(["search", "--all", "--unlock", KeyAttribute, key], null);
        if (result.Output.Length == 0) return null;
        var pat = Field(result.Output, "secret") ??
            throw new CredentialStoreUnavailableException("The Secret Service entry could not be unlocked.");
        var login = Field(result.Output, "attribute." + LoginAttribute) ??
            Field(result.Error, "attribute." + LoginAttribute) ?? "";
        return new RepositoryCredentials(login, pat);
    }

    public void Write(string key, RepositoryCredentials credentials)
    {
        // An entry stored with the same attributes replaces the previous one; one stored for another login is
        // deleted once the new one exists, so a failure leaves the previous credentials in place.
        var previous = Read(key);
        List<string> arguments = ["store", $"--label=depinv {key}", KeyAttribute, key];
        if (credentials.Login.Length > 0) arguments.AddRange([LoginAttribute, credentials.Login]);
        Run(arguments, credentials.Pat);
        if (previous is null || previous.Login.Length == 0 || previous.Login == credentials.Login) return;

        try
        {
            Clear([KeyAttribute, key, LoginAttribute, previous.Login]);
        }
        catch (CredentialStoreUnavailableException) when (credentials.Login.Length > 0)
        {
            // Two entries would share the key and a scan could read either: the new one is removed, so the
            // previous credentials stay the only ones, as when the store itself fails.
            try
            {
                Clear([KeyAttribute, key, LoginAttribute, credentials.Login]);
            }
            catch (CredentialStoreUnavailableException)
            {
                // The first failure is the one reported.
            }
            throw;
        }
    }

    public bool Delete(string key)
    {
        // The exit code of clear says whether it ran, not whether an entry matched: search tells.
        if (Run(["search", "--all", KeyAttribute, key], null).Output.Length == 0) return false;
        Clear([KeyAttribute, key]);
        return true;
    }

    /// <summary>
    /// Deletes the matching entries. Some versions of secret-tool end with 1, saying nothing, when none matches:
    /// it is not an error.
    /// </summary>
    private void Clear(IReadOnlyList<string> attributes)
    {
        var result = _run(["clear", .. attributes], null);
        if (result.ExitCode != 0 && !(result.ExitCode == 1 && result.Error.Trim().Length == 0))
            throw Unavailable(result);
    }

    private SecretToolResult Run(IReadOnlyList<string> arguments, string? input)
    {
        var result = _run(arguments, input);
        return result.ExitCode == 0 ? result : throw Unavailable(result);
    }

    private static CredentialStoreUnavailableException Unavailable(SecretToolResult result) =>
        new($"The Secret Service is not available: {FirstLine(result.Error) ?? $"secret-tool exit code {result.ExitCode}"}");

    /// <summary>The first line of a secret-tool message, without its "secret-tool: " prefix.</summary>
    private static string? FirstLine(string text)
    {
        const string prefix = "secret-tool: ";
        var line = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return line?.StartsWith(prefix, StringComparison.Ordinal) == true ? line[prefix.Length..] : line;
    }

    /// <summary>The value of the first "name = value" line, or null.</summary>
    private static string? Field(string text, string name)
    {
        var prefix = name + " = ";
        return text.Split('\n').Select(line => line.TrimEnd('\r'))
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    private static SecretToolResult RunSecretTool(IReadOnlyList<string> arguments, string? input)
    {
        var start = new System.Diagnostics.ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(start)!;
        }
        catch (Win32Exception ex)
        {
            throw new CredentialStoreUnavailableException(
                "The Secret Service is not available: secret-tool was not found. Install it (package " +
                "libsecret-tools on Debian and Ubuntu, libsecret elsewhere).", ex);
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (input is not null) process.StandardInput.Write(input);
            process.StandardInput.Close();
            process.WaitForExit();
            return new SecretToolResult(process.ExitCode, output.Result, error.Result);
        }
    }
}

public sealed record SecretToolResult(int ExitCode, string Output, string Error);

/// <summary>
/// Generic passwords of the macOS default keychain (the login keychain), through the Security framework: the entry
/// key is the service, the login the account, the PAT the password. An entry is replaced in place, account and
/// password together, so a failure leaves the previous credentials whole. Every operation targets the default
/// keychain alone: an entry of the same service in another keychain of the search list is ignored.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainStore : ICredentialStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ItemNotFound = -25300;          // errSecItemNotFound
    private const uint AccountAttribute = 0x61636374; // kSecAccountItemAttr ('acct')
    private const uint Utf8Encoding = 0x08000100;     // kCFStringEncodingUTF8
    private const string NoLogin = "-";               // Stored for GitLab, whose PAT has no login.

    public string Name => "macOS Keychain";

    public bool AcceptsCredentialsFiles => true;

    public RepositoryCredentials? Read(string key) => InDefaultKeychain(keychain =>
    {
        var item = Find(keychain, key, out var pat);
        if (item == IntPtr.Zero) return null;
        try
        {
            var login = Account(item);
            return new RepositoryCredentials(login == NoLogin ? "" : login, pat);
        }
        finally
        {
            CFRelease(item);
        }
    });

    public void Write(string key, RepositoryCredentials credentials) => InDefaultKeychain(keychain =>
    {
        var account = Encoding.UTF8.GetBytes(credentials.Login.Length == 0 ? NoLogin : credentials.Login);
        var secret = Encoding.UTF8.GetBytes(credentials.Pat);
        try
        {
            var item = Find(keychain, key, out _);
            if (item == IntPtr.Zero)
            {
                var service = Encoding.UTF8.GetBytes(key);
                Check(SecKeychainAddGenericPassword(keychain, (uint)service.Length, service,
                    (uint)account.Length, account, (uint)secret.Length, secret, IntPtr.Zero));
                return true;
            }

            var data = Marshal.AllocHGlobal(account.Length);
            var attributes = Marshal.AllocHGlobal(Marshal.SizeOf<KeychainAttribute>());
            try
            {
                Marshal.Copy(account, 0, data, account.Length);
                Marshal.StructureToPtr(
                    new KeychainAttribute { Tag = AccountAttribute, Length = (uint)account.Length, Data = data },
                    attributes, false);
                var list = new KeychainAttributeList { Count = 1, Attributes = attributes };
                Check(SecKeychainItemModifyAttributesAndData(item, ref list, (uint)secret.Length, secret));
            }
            finally
            {
                Marshal.FreeHGlobal(attributes);
                Marshal.FreeHGlobal(data);
                CFRelease(item);
            }
        }
        finally
        {
            Array.Clear(secret);
        }
        return true;
    });

    public bool Delete(string key) => InDefaultKeychain(keychain =>
    {
        var item = Find(keychain, key, out _);
        if (item == IntPtr.Zero) return false;
        try
        {
            Check(SecKeychainItemDelete(item));
            return true;
        }
        finally
        {
            CFRelease(item);
        }
    });

    /// <summary>
    /// Runs the operation on the default keychain, the one entries are added to. Searching without a keychain would
    /// look through the whole search list, and could read or change an entry of another keychain.
    /// </summary>
    private static T InDefaultKeychain<T>(Func<IntPtr, T> operation)
    {
        Check(SecKeychainCopyDefault(out var keychain));
        try
        {
            return operation(keychain);
        }
        finally
        {
            CFRelease(keychain);
        }
    }

    /// <summary>
    /// The entry of the key in the keychain, whatever its account, and its password; IntPtr.Zero when none exists.
    /// </summary>
    private static IntPtr Find(IntPtr keychain, string key, out string password)
    {
        var service = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(keychain, (uint)service.Length, service, 0, null,
            out var length, out var data, out var item);
        password = "";
        if (status == ItemNotFound) return IntPtr.Zero;
        Check(status);
        try
        {
            if (length > 0) password = Marshal.PtrToStringUTF8(data, (int)length);
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
        }
        return item;
    }

    private static string Account(IntPtr item)
    {
        var attributes = Marshal.AllocHGlobal(Marshal.SizeOf<KeychainAttribute>());
        try
        {
            Marshal.StructureToPtr(new KeychainAttribute { Tag = AccountAttribute }, attributes, false);
            var list = new KeychainAttributeList { Count = 1, Attributes = attributes };
            Check(SecKeychainItemCopyContent(item, IntPtr.Zero, ref list, IntPtr.Zero, IntPtr.Zero));
            try
            {
                var account = Marshal.PtrToStructure<KeychainAttribute>(attributes);
                return account.Length == 0 ? "" : Marshal.PtrToStringUTF8(account.Data, (int)account.Length);
            }
            finally
            {
                SecKeychainItemFreeContent(ref list, IntPtr.Zero);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(attributes);
        }
    }

    /// <summary>Any failure but a missing entry, such as a locked keychain over SSH, makes the vault unavailable.</summary>
    private static void Check(int status)
    {
        if (status != 0)
            throw new CredentialStoreUnavailableException($"The macOS Keychain is not available: {Message(status)}");
    }

    private static string Message(int status)
    {
        var message = SecCopyErrorMessageString(status, IntPtr.Zero);
        if (message == IntPtr.Zero) return $"OSStatus {status}.";
        try
        {
            var buffer = new byte[1024];
            if (!CFStringGetCString(message, buffer, buffer.Length, Utf8Encoding)) return $"OSStatus {status}.";
            var end = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
        }
        finally
        {
            CFRelease(message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeychainAttribute
    {
        public uint Tag;
        public uint Length;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeychainAttributeList
    {
        public uint Count;
        public IntPtr Attributes;
    }

    [DllImport(Security)]
    private static extern int SecKeychainCopyDefault(out IntPtr keychain);

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength,
        byte[] serviceName, uint accountNameLength, byte[]? accountName, out uint passwordLength,
        out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength,
        byte[] serviceName, uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData,
        IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemCopyContent(IntPtr itemRef, IntPtr itemClass,
        ref KeychainAttributeList attrList, IntPtr length, IntPtr outData);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef,
        ref KeychainAttributeList attrList, uint length, byte[] data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(ref KeychainAttributeList attrList, IntPtr data);

    [DllImport(Security)]
    private static extern IntPtr SecCopyErrorMessageString(int status, IntPtr reserved);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
