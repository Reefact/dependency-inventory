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
