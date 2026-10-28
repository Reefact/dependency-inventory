using Xunit;

namespace DependencyInventory.Tests;

public sealed class HelpTests
{
    // Option descriptions are Spectre markup: a bracketed word, such as [/project], breaks the help.
    [Theory]
    [InlineData("packages")]
    [InlineData("frameworks")]
    [InlineData("feed")]
    [InlineData("login")]
    [InlineData("logout")]
    public async Task TheHelpOfEveryCommandRenders(string command)
    {
        Assert.Equal(0, await DepinvApp.Create().RunAsync([command, "--help"]));
    }
}
