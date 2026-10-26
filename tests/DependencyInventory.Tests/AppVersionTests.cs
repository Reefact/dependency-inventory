using Xunit;

namespace DependencyInventory.Tests;

public sealed class AppVersionTests
{
    [Fact]
    public void Current_is_not_empty_and_carries_no_build_metadata()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppVersion.Current));
        Assert.DoesNotContain('+', AppVersion.Current);
    }
}
