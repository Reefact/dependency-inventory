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

    [Fact]
    public async Task ThePackageScanIsNoLongerTheDefaultCommand()
    {
        var exitCode = await DepinvApp.Create().RunAsync(
            ["--provider", "azure", "--url", "https://example.com/not-azure"]);

        Assert.NotEqual(0, exitCode);
        Assert.NotEqual(2, exitCode);
    }
}
