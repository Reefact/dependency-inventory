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

    [Fact]
    public async Task ThePackageScanIsNoLongerTheDefaultCommand()
    {
        var exitCode = await DepinvApp.Create().RunAsync(
            ["--provider", "azure", "--url", "https://example.com/not-azure"]);

        Assert.NotEqual(0, exitCode);
        Assert.NotEqual(2, exitCode);
    }
}
