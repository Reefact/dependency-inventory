using Xunit;

namespace DependencyInventory.Tests;

public sealed class MacKeychainStoreTests
{
    [MacKeychainFact]
    public void TheKeychainKeepsLoginAndPat()
    {
        var store = CredentialStores.ForCurrentPlatform()!;
        var key = $"dependency-inventory:test:{Guid.NewGuid():N}";
        try
        {
            Assert.Equal("macOS Keychain", store.Name);
            Assert.Null(store.Read(key));

            store.Write(key, new RepositoryCredentials("alice@company.com", "alice-pat"));
            Assert.Equal(new RepositoryCredentials("alice@company.com", "alice-pat"), store.Read(key));

            // Another login replaces the entry: no second entry is left for the key.
            store.Write(key, new RepositoryCredentials("élodie dupont", "bob-pat \"quoted\""));
            Assert.Equal(new RepositoryCredentials("élodie dupont", "bob-pat \"quoted\""), store.Read(key));

            Assert.True(store.Delete(key));
            Assert.Null(store.Read(key));
            Assert.False(store.Delete(key));

            store.Write(key, new RepositoryCredentials("", "gitlab-pat"));
            Assert.Equal(new RepositoryCredentials("", "gitlab-pat"), store.Read(key));
        }
        finally
        {
            store.Delete(key);
        }
    }

    [MacKeychainFact]
    public void AnEntryOfAnotherKeychainOfTheSearchListIsIgnored()
    {
        var store = CredentialStores.ForCurrentPlatform()!;
        var key = $"dependency-inventory:test:{Guid.NewGuid():N}";
        var other = Path.Combine(Path.GetTempPath(), $"depinv-other-{Guid.NewGuid():N}.keychain");
        var searchList = Security("list-keychains", "-d", "user").Split('\n',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim('"')).ToArray();
        Security("create-keychain", "-p", "test", other);
        try
        {
            Security("unlock-keychain", "-p", "test", other);
            // The other keychain comes first in the search list and holds the same service.
            Security(["list-keychains", "-d", "user", "-s", other, .. searchList]);
            Security("add-generic-password", "-s", key, "-a", "old@company.com", "-w", "old-pat", other);

            Assert.Null(store.Read(key));
            Assert.False(store.Delete(key));

            store.Write(key, new RepositoryCredentials("new@company.com", "new-pat"));
            Assert.Equal(new RepositoryCredentials("new@company.com", "new-pat"), store.Read(key));
            Assert.True(store.Delete(key));

            Assert.Contains("old-pat", Security("find-generic-password", "-s", key, "-w", other));
        }
        finally
        {
            store.Delete(key);
            Security(["list-keychains", "-d", "user", "-s", .. searchList]);
            Security("delete-keychain", other);
        }
    }

    [MacKeychainFact]
    public async Task TheCredentialsFileIsStillAccepted()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "carol@company.com\nfile-pat");

            var resolved = await CredentialResolver.ResolveAsync(SourceProvider.Azure,
                "https://dev.azure.com/Acme/Project", true, path, CredentialStores.ForCurrentPlatform(),
                () => null, CancellationToken.None);

            Assert.Equal(CredentialSource.File, resolved.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Runs the security command of macOS; its standard output, or a failed assertion.</summary>
    private static string Security(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"security {string.Join(' ', arguments)}: {error}");
        return output.Result;
    }
}

/// <summary>
/// A test that needs an unlocked default keychain; run on macOS where DEPINV_TEST_KEYCHAIN is set, as in the macOS
/// CI leg, and skipped elsewhere.
/// </summary>
public sealed class MacKeychainFactAttribute : FactAttribute
{
    public MacKeychainFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("DEPINV_TEST_KEYCHAIN") != "1")
            Skip = "Needs the macOS Keychain: set DEPINV_TEST_KEYCHAIN=1 on macOS with an unlocked default keychain.";
    }
}
