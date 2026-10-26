using Xunit;

namespace DependencyInventory.Tests;

public sealed class CredentialStoreTests
{
    private const string AzureUrl = "https://dev.azure.com/Acme/Project";
    private static readonly RepositoryCredentials Stored = new("alice@company.com", "stored-pat");
    private static readonly RepositoryCredentials Prompted = new("bob@company.com", "prompted-pat");

    [Theory]
    [InlineData(SourceProvider.Azure, "https://dev.azure.com/Acme/Project", "dependency-inventory:azure:dev.azure.com/acme")]
    [InlineData(SourceProvider.Azure, "https://dev.azure.com/acme/Other", "dependency-inventory:azure:dev.azure.com/acme")]
    [InlineData(SourceProvider.GitLab, "https://GitLab.Example.com/red/web", "dependency-inventory:gitlab:gitlab.example.com")]
    [InlineData(SourceProvider.GitLab, "https://gitlab.example.com:8443/red", "dependency-inventory:gitlab:gitlab.example.com:8443")]
    public void KeyIsPerProviderAndOrigin(SourceProvider provider, string url, string expected)
    {
        Assert.Equal(expected, CredentialStores.KeyFor(provider, url));
    }

    [Fact]
    public void KeyRejectsAnInvalidOrigin()
    {
        Assert.Throws<ArgumentException>(() => CredentialStores.KeyFor(SourceProvider.Azure, "https://example.com/x"));
    }

    [Fact]
    public async Task WithoutAVaultTheCredentialsFileIsRead()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "carol@company.com\nfile-pat");

            var resolved = await ResolveAsync(store: null, useFile: true, path);

            Assert.Equal(CredentialSource.File, resolved.Source);
            Assert.Equal(new RepositoryCredentials("carol@company.com", "file-pat"), resolved.Credentials);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WithAVaultTheCredentialsFileIsRefused()
    {
        var store = new MemoryStore { [CredentialStores.KeyFor(SourceProvider.Azure, AzureUrl)] = Stored };

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => ResolveAsync(store, useFile: true, "unused"));
        Assert.Contains("dependency-inventory login", exception.Message);
    }

    [Fact]
    public async Task TheVaultComesBeforeThePrompt()
    {
        var store = new MemoryStore { [CredentialStores.KeyFor(SourceProvider.Azure, AzureUrl)] = Stored };

        var resolved = await ResolveAsync(store);

        Assert.Equal(CredentialSource.Store, resolved.Source);
        Assert.Equal(Stored, resolved.Credentials);
    }

    [Fact]
    public async Task WithoutAnEntryTheUserIsPromptedAndNothingIsStored()
    {
        var store = new MemoryStore();

        var resolved = await ResolveAsync(store);

        Assert.Equal(CredentialSource.Prompt, resolved.Source);
        Assert.Equal(Prompted, resolved.Credentials);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task WithoutAVaultTheUserIsPrompted()
    {
        var resolved = await ResolveAsync(store: null);

        Assert.Equal(CredentialSource.Prompt, resolved.Source);
    }

    [Fact]
    public async Task AnEntryOfAnotherOriginIsNotUsed()
    {
        var store = new MemoryStore
        {
            [CredentialStores.KeyFor(SourceProvider.Azure, "https://dev.azure.com/other/project")] = Stored
        };

        var resolved = await ResolveAsync(store);

        Assert.Equal(CredentialSource.Prompt, resolved.Source);
    }

    [WindowsFact]
    public void TheWindowsCredentialManagerKeepsLoginAndPat()
    {
        var store = CredentialStores.ForCurrentPlatform()!;
        var key = $"dependency-inventory:test:{Guid.NewGuid():N}";
        try
        {
            Assert.Null(store.Read(key));

            store.Write(key, Stored);
            Assert.Equal(Stored, store.Read(key));

            store.Write(key, new RepositoryCredentials("", "gitlab-pat"));
            Assert.Equal(new RepositoryCredentials("", "gitlab-pat"), store.Read(key));

            Assert.True(store.Delete(key));
            Assert.Null(store.Read(key));
            Assert.False(store.Delete(key));
        }
        finally
        {
            store.Delete(key);
        }
    }

    private static Task<ResolvedCredentials> ResolveAsync(ICredentialStore? store, bool useFile = false,
        string? path = null) =>
        CredentialResolver.ResolveAsync(SourceProvider.Azure, AzureUrl, useFile, path, store,
            () => Prompted, CancellationToken.None);
}

/// <summary>A test that needs the Windows Credential Manager; skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Needs the Windows Credential Manager.";
    }
}
