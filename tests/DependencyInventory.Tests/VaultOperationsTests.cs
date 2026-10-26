using Xunit;

namespace DependencyInventory.Tests;

public sealed class VaultOperationsTests
{
    private const string Key = "dependency-inventory:azure:dev.azure.com/acme";
    private static readonly RepositoryCredentials Previous = new("alice@company.com", "previous-pat");
    private static readonly RepositoryCredentials Typed = new("alice@company.com", "typed-pat");

    [Fact]
    public void LoginStoresTheTypedCredentials()
    {
        var store = new MemoryStore();

        Assert.Equal(VaultChange.Stored, VaultOperations.Login(store, Key, () => Typed, CancellationToken.None));
        Assert.Equal(Typed, store.Read(Key));
    }

    [Fact]
    public void LoginCancelledDuringThePromptLeavesTheVaultUnchanged()
    {
        var store = new MemoryStore { [Key] = Previous };
        using var cancellation = new CancellationTokenSource();

        var change = VaultOperations.Login(store, Key, () =>
        {
            cancellation.Cancel(); // Ctrl+C while the prompt is shown
            return Typed;
        }, cancellation.Token);

        Assert.Equal(VaultChange.Cancelled, change);
        Assert.Equal(0, store.Writes);
        Assert.Equal(Previous, store.Read(Key));
    }

    [Fact]
    public void LoginWithoutCredentialsStoresNothing()
    {
        var store = new MemoryStore();

        Assert.Equal(VaultChange.MissingCredentials,
            VaultOperations.Login(store, Key, () => null, CancellationToken.None));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void LogoutDeletesTheEntry()
    {
        var store = new MemoryStore { [Key] = Previous };

        Assert.Equal(VaultChange.Deleted, VaultOperations.Logout(store, Key, CancellationToken.None));
        Assert.Null(store.Read(Key));
        Assert.Equal(VaultChange.NotFound, VaultOperations.Logout(store, Key, CancellationToken.None));
    }

    [Fact]
    public void LogoutCancelledLeavesTheVaultUnchanged()
    {
        var store = new MemoryStore { [Key] = Previous };

        Assert.Equal(VaultChange.Cancelled, VaultOperations.Logout(store, Key, new CancellationToken(canceled: true)));
        Assert.Equal(0, store.Deletes);
        Assert.Equal(Previous, store.Read(Key));
    }
}
