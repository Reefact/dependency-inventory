using Xunit;

namespace DependencyInventory.Tests;

public sealed class SecretServiceStoreTests
{
    private const string Key = "dependency-inventory:azure:dev.azure.com/acme";

    [Fact]
    public void ThePatIsPassedOnStandardInputNotOnTheCommandLine()
    {
        var secretTool = new FakeSecretTool();
        var store = new SecretServiceStore(secretTool.Run);

        store.Write(Key, new RepositoryCredentials("alice@company.com", "the-pat"));

        var call = Assert.Single(secretTool.Calls, c => c.Arguments[0] == "store");
        Assert.Equal("the-pat", call.Input);
        Assert.DoesNotContain(call.Arguments, argument => argument.Contains("the-pat"));
        Assert.Equal(["store", $"--label=depinv {Key}", "key", Key, "login", "alice@company.com"], call.Arguments);
    }

    [Fact]
    public void AnEntryIsReadFromTheSecretAndItsLoginAttribute()
    {
        var secretTool = new FakeSecretTool();
        secretTool.Respond("search", new(0,
            "[/1]\nlabel = depinv\nsecret = the-pat\ncreated = 2026-10-28 14:11:57\n",
            $"attribute.key = {Key}\nattribute.login = alice@company.com\n"));

        var credentials = new SecretServiceStore(secretTool.Run).Read(Key);

        Assert.Equal(new RepositoryCredentials("alice@company.com", "the-pat"), credentials);
    }

    [Fact]
    public void AnEntryWithoutLoginHasAnEmptyLogin()
    {
        var secretTool = new FakeSecretTool();
        secretTool.Respond("search", new(0, "[/1]\nsecret = gitlab-pat\n", $"attribute.key = {Key}\n"));

        Assert.Equal(new RepositoryCredentials("", "gitlab-pat"), new SecretServiceStore(secretTool.Run).Read(Key));
    }

    [Fact]
    public void NoEntryReadsAsNull()
    {
        Assert.Null(new SecretServiceStore(new FakeSecretTool().Run).Read(Key));
    }

    [Fact]
    public void ALockedEntryIsReportedUnavailable()
    {
        var secretTool = new FakeSecretTool();
        secretTool.Respond("search", new(0, "[/1]\nlabel = depinv\n", ""));

        Assert.Throws<CredentialStoreUnavailableException>(() => new SecretServiceStore(secretTool.Run).Read(Key));
    }

    [Fact]
    public void AFailureOfSecretToolIsReportedUnavailableWithItsMessage()
    {
        var secretTool = new FakeSecretTool();
        secretTool.Respond("search", new(1, "", "secret-tool: Cannot autolaunch D-Bus without X11 $DISPLAY\n"));

        var exception = Assert.Throws<CredentialStoreUnavailableException>(
            () => new SecretServiceStore(secretTool.Run).Read(Key));
        Assert.Contains("Cannot autolaunch D-Bus", exception.Message);
    }

    [Fact]
    public void DeleteTellsWhetherAnEntryExisted()
    {
        var secretTool = new FakeSecretTool();
        var store = new SecretServiceStore(secretTool.Run);
        secretTool.Respond("clear", new(0, "", ""));

        secretTool.Respond("search", new(0, "[/1]\nlabel = depinv\n", $"attribute.key = {Key}\n"));
        Assert.True(store.Delete(Key));
        Assert.Equal(["search", "--all", "key", Key], secretTool.Calls[0].Arguments);
        Assert.Equal(["clear", "key", Key], secretTool.Calls[1].Arguments);

        secretTool.Respond("clear", new(1, "", "secret-tool: Cannot autolaunch D-Bus without X11 $DISPLAY\n"));
        Assert.Throws<CredentialStoreUnavailableException>(() => store.Delete(Key));
    }

    [Fact]
    public void WithoutAnEntryDeleteIsFalseWhateverClearWouldSay()
    {
        var secretTool = new FakeSecretTool();
        secretTool.Respond("clear", new(0, "", "")); // Some versions of secret-tool succeed when nothing matches.

        Assert.False(new SecretServiceStore(secretTool.Run).Delete(Key));
        Assert.DoesNotContain(secretTool.Calls, c => c.Arguments[0] == "clear");
    }

    [Fact]
    public void AnUnreachableVaultIsReportedOnDelete()
    {
        var secretTool = new FakeSecretTool();
        secretTool.Respond("search", new(1, "", "secret-tool: Cannot autolaunch D-Bus without X11 $DISPLAY\n"));

        Assert.Throws<CredentialStoreUnavailableException>(() => new SecretServiceStore(secretTool.Run).Delete(Key));
    }

    [Fact]
    public void AnEntryOfAnotherLoginIsDeletedOnceTheNewOneIsStored()
    {
        var secretTool = new FakeSecretTool();
        secretTool.Respond("search", new(0, "[/1]\nsecret = old-pat\n", "attribute.login = alice@company.com\n"));
        secretTool.Respond("clear", new(0, "", ""));

        new SecretServiceStore(secretTool.Run).Write(Key, new RepositoryCredentials("bob@company.com", "new-pat"));

        Assert.Equal(["search", "store", "clear"], secretTool.Calls.Select(c => c.Arguments[0]));
        Assert.Equal(["clear", "key", Key, "login", "alice@company.com"], secretTool.Calls[^1].Arguments);
    }

    [Fact]
    public void AFailedStoreKeepsThePreviousEntry()
    {
        var secretTool = new FakeSecretTool();
        secretTool.Respond("search", new(0, "[/1]\nsecret = old-pat\n", "attribute.login = alice@company.com\n"));
        secretTool.Respond("store", new(1, "", "secret-tool: Cannot create an item in a locked collection\n"));

        Assert.Throws<CredentialStoreUnavailableException>(() => new SecretServiceStore(secretTool.Run)
            .Write(Key, new RepositoryCredentials("bob@company.com", "new-pat")));
        Assert.DoesNotContain(secretTool.Calls, c => c.Arguments[0] == "clear");
    }

    [Fact]
    public async Task AnUnavailableVaultFallsBackToThePromptWithItsReason()
    {
        var secretTool = new FakeSecretTool();
        secretTool.Respond("search", new(1, "", "secret-tool: Cannot autolaunch D-Bus without X11 $DISPLAY\n"));
        var prompted = new RepositoryCredentials("bob@company.com", "prompted-pat");

        var resolved = await CredentialResolver.ResolveAsync(SourceProvider.Azure, "https://dev.azure.com/Acme/Project",
            false, null, new SecretServiceStore(secretTool.Run), () => prompted, CancellationToken.None);

        Assert.Equal(CredentialSource.Prompt, resolved.Source);
        Assert.Equal(prompted, resolved.Credentials);
        Assert.Contains("Cannot autolaunch D-Bus", resolved.StoreUnavailable);
    }

    [Fact]
    public void LoginReportsAnUnavailableVaultBeforePrompting()
    {
        var secretTool = new FakeSecretTool();
        secretTool.Respond("search", new(1, "", "secret-tool: Cannot autolaunch D-Bus without X11 $DISPLAY\n"));
        var prompted = false;

        Assert.Throws<CredentialStoreUnavailableException>(() => VaultOperations.Login(
            new SecretServiceStore(secretTool.Run), Key, () =>
            {
                prompted = true;
                return null;
            }, CancellationToken.None));
        Assert.False(prompted);
    }

    [Fact]
    public async Task TheCredentialsFileIsStillAccepted()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "carol@company.com\nfile-pat");
            var secretTool = new FakeSecretTool();

            var resolved = await CredentialResolver.ResolveAsync(SourceProvider.Azure,
                "https://dev.azure.com/Acme/Project", true, path, new SecretServiceStore(secretTool.Run),
                () => null, CancellationToken.None);

            Assert.Equal(CredentialSource.File, resolved.Source);
            Assert.Empty(secretTool.Calls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SecretServiceFact]
    public void TheSecretServiceKeepsLoginAndPat()
    {
        var store = new SecretServiceStore();
        var key = $"dependency-inventory:test:{Guid.NewGuid():N}";
        try
        {
            Assert.Null(store.Read(key));

            store.Write(key, new RepositoryCredentials("alice@company.com", "alice-pat"));
            Assert.Equal(new RepositoryCredentials("alice@company.com", "alice-pat"), store.Read(key));

            store.Write(key, new RepositoryCredentials("bob@company.com", "bob-pat"));
            Assert.Equal(new RepositoryCredentials("bob@company.com", "bob-pat"), store.Read(key));

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

    private sealed class FakeSecretTool
    {
        private readonly Dictionary<string, SecretToolResult> _responses = new()
        {
            ["search"] = new(0, "", ""),
            ["store"] = new(0, "", ""),
            ["clear"] = new(1, "", "")
        };

        public List<(string[] Arguments, string? Input)> Calls { get; } = [];

        public void Respond(string command, SecretToolResult result) => _responses[command] = result;

        public SecretToolResult Run(IReadOnlyList<string> arguments, string? input)
        {
            Calls.Add(([.. arguments], input));
            return _responses[arguments[0]];
        }
    }
}

/// <summary>
/// A test that needs a running and unlocked Secret Service; run where DEPINV_TEST_SECRET_SERVICE is set, as in the
/// Linux CI leg, and skipped elsewhere.
/// </summary>
public sealed class SecretServiceFactAttribute : FactAttribute
{
    public SecretServiceFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("DEPINV_TEST_SECRET_SERVICE") != "1")
            Skip = "Needs a Secret Service: set DEPINV_TEST_SECRET_SERVICE=1 on Linux with an unlocked keyring.";
    }
}
