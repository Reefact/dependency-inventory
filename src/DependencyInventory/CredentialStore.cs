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
}

public static class CredentialStores
{
    /// <summary>The vault of the current system, or null where none is supported yet.</summary>
    public static ICredentialStore? ForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsCredentialStore() : null;

    /// <summary>
    /// One entry per provider and origin: an Azure DevOps PAT belongs to an organization,
    /// a GitLab PAT to an instance.
    /// </summary>
    public static string KeyFor(SourceProvider provider, string url) => provider switch
    {
        SourceProvider.Azure =>
            $"dependency-inventory:azure:dev.azure.com/{AzureOrigin.Parse(url).Organization.ToLowerInvariant()}",
        SourceProvider.GitLab =>
            $"dependency-inventory:gitlab:{new Uri(GitLabOrigin.Parse(url).BaseUrl).Authority.ToLowerInvariant()}",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}

public enum CredentialSource { File, Store, Prompt }

public sealed record ResolvedCredentials(RepositoryCredentials? Credentials, CredentialSource Source);

public static class CredentialResolver
{
    /// <summary>
    /// Credentials file when requested, then the vault entry, then the prompt. Never writes to the vault:
    /// only the login command does. File errors are left to the caller.
    /// </summary>
    public static async Task<ResolvedCredentials> ResolveAsync(SourceProvider provider, string url,
        bool useFile, string? filePath, ICredentialStore? store, Func<RepositoryCredentials?> prompt,
        CancellationToken ct)
    {
        if (useFile)
        {
            var path = CredentialsFile.ResolvePath(provider, filePath);
            return new(await CredentialsFile.ReadAsync(path, provider, ct), CredentialSource.File);
        }

        if (store?.Read(CredentialStores.KeyFor(provider, url)) is { } stored)
            return new(stored, CredentialSource.Store);

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
