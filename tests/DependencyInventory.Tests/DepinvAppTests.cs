using Xunit;

namespace DependencyInventory.Tests;

public sealed class DepinvAppTests
{
    // An invalid URL stops the command with exit code 2 before any credential or network access.
    [Theory]
    [InlineData("packages")]
    [InlineData("pkg")]
    [InlineData("frameworks")]
    [InlineData("fwk")]
    [InlineData("feed")]
    public async Task EveryInventoryRunsUnderItsCommandAndAlias(string command)
    {
        var exitCode = await DepinvApp.Create().RunAsync(
            [command, "--provider", "azure", "--url", "https://example.com/not-azure"]);

        Assert.Equal(2, exitCode);
    }

    // Spectre rejects an unknown option before the command runs, with neither 0 nor the command's 2.
    [Theory]
    [InlineData("packages", "--patern")]
    [InlineData("frameworks", "--pattern")]
    public async Task AnUnknownOptionIsRejected(string command, string option)
    {
        var exitCode = await DepinvApp.Create().RunAsync(
            [command, "--provider", "azure", "--url", "https://example.com/not-azure", option, "A"]);

        Assert.NotEqual(0, exitCode);
        Assert.NotEqual(2, exitCode);
    }

    // Invalid input stops the feed command with 2 before any credential or network access.
    [Theory]
    [InlineData("--provider", "gitlab", "--url", "https://gitlab.example.com/group", "--feed", "f", "--package", "A")]
    [InlineData("--provider", "azure", "--url", "https://dev.azure.com/acme/project", "--package", "A")]
    [InlineData("--provider", "azure", "--url", "https://dev.azure.com/acme/project", "--feed", "f")]
    [InlineData("--provider", "azure", "--url", "https://dev.azure.com/acme/project", "--feed", "f", "--package", "A@x")]
    [InlineData("--provider", "azure", "--url", "https://dev.azure.com/acme/project", "--feed", "f", "--package", "A",
        "--parallelism", "9")]
    [InlineData("--provider", "azure", "--url", "https://dev.azure.com/acme/project", "--feed", "f",
        "--versions-from", "missing-dependencies.csv")]
    public async Task TheFeedCommandRejectsInvalidInput(params string[] arguments)
    {
        Assert.Equal(2, await DepinvApp.Create().RunAsync(["feed", .. arguments]));
    }

    // Invalid branch options stop both repository inventories with 2 before any credential or network access.
    [Theory]
    [InlineData("packages", "--pattern", "A", "--branch", "main", "--branch-file", "branches.txt")]
    [InlineData("packages", "--pattern", "A", "--branch-file", "missing-branches.txt")]
    [InlineData("frameworks", "--branch", "main", "--branch-file", "branches.txt")]
    [InlineData("frameworks", "--branch-file", "missing-branches.txt")]
    public async Task InvalidBranchOptionsAreRejected(params string[] arguments)
    {
        Assert.Equal(2, await DepinvApp.Create().RunAsync(
            [arguments[0], "--provider", "azure", "--url", "https://dev.azure.com/acme/project", .. arguments[1..]]));
    }

    // Invalid package sources stop the package scan with 2 before any credential or network access.
    [Theory]
    [InlineData("--provider", "azure", "--url", "https://dev.azure.com/acme/project", "--pattern", "A", "--no-nuget-org")]
    [InlineData("--provider", "gitlab", "--url", "https://gitlab.example.com/group", "--pattern", "A", "--feed", "f")]
    [InlineData("--provider", "azure", "--url", "https://dev.azure.com/acme/project", "--pattern", "A",
        "--feed", "https://example.com/acme/_packaging/f")]
    [InlineData("--provider", "azure", "--url", "https://dev.azure.com/acme/project", "--pattern", "A",
        "--feed", "f", "--feed", "a/b/c")]
    public async Task ThePackageScanRejectsInvalidPackageSources(params string[] arguments)
    {
        Assert.Equal(2, await DepinvApp.Create().RunAsync(["packages", .. arguments]));
    }

    // A known option reaches the command, which stops on the invalid URL with 2.
    [Theory]
    [InlineData("--transitive")]
    [InlineData("--used-types")]
    [InlineData("--no-nuget-org")]
    public async Task ThePackageScanAcceptsItsOptions(string option)
    {
        Assert.Equal(2, await DepinvApp.Create().RunAsync(
            ["packages", "--provider", "azure", "--url", "https://example.com/not-azure", "--pattern", "A", option]));
    }

    [Fact]
    public async Task TheFeedCommandRejectsAnInvalidFeedUrl()
    {
        Assert.Equal(2, await DepinvApp.Create().RunAsync(["feed", "--provider", "azure",
            "--url", "https://dev.azure.com/acme/project", "--feed", "https://dev.azure.com/acme/libs", "--package", "A"]));
    }

    [Fact]
    public async Task ThePackageScanIsNoLongerTheDefaultCommand()
    {
        var exitCode = await DepinvApp.Create().RunAsync(
            ["--provider", "azure", "--url", "https://example.com/not-azure"]);

        Assert.NotEqual(0, exitCode);
        Assert.NotEqual(2, exitCode);
    }
}
