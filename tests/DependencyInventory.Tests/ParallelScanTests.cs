using System.Text.Json;
using Xunit;

namespace DependencyInventory.Tests;

public sealed class ParallelScanTests
{
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(10);

    [Theory]
    [InlineData(1, null)]
    [InlineData(4, ParallelismMode.Files)]
    [InlineData(8, ParallelismMode.Repositories)]
    public void ValidCombinationsAreAccepted(int degree, ParallelismMode? mode)
    {
        var parallelism = ScanParallelism.Create(degree, mode);
        Assert.Equal(degree, parallelism.Degree);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(9, ParallelismMode.Files)]
    [InlineData(-1, ParallelismMode.Repositories)]
    [InlineData(4, null)]
    [InlineData(1, ParallelismMode.Files)]
    public void InvalidCombinationsAreRejected(int degree, ParallelismMode? mode)
    {
        Assert.Throws<ArgumentException>(() => ScanParallelism.Create(degree, mode));
    }

    [Theory]
    [InlineData(ParallelismMode.Files)]
    [InlineData(ParallelismMode.Repositories)]
    public async Task ParallelScansGiveTheSequentialResult(ParallelismMode mode)
    {
        var sequential = await ScanAsync(RepositoryScannerTests.Provider(Latency), ScanParallelism.Sequential);
        var parallel = await ScanAsync(RepositoryScannerTests.Provider(Latency), ScanParallelism.Create(4, mode));

        Assert.Equal(Serialize(sequential), Serialize(parallel));
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(3, ParallelismMode.Files)]
    [InlineData(4, ParallelismMode.Files)]
    [InlineData(3, ParallelismMode.Repositories)]
    [InlineData(4, ParallelismMode.Repositories)]
    public async Task SimultaneousRequestsNeverExceedTheDegree(int degree, ParallelismMode? mode)
    {
        var api = ManyRepositories();

        await ScanAsync(api, ScanParallelism.Create(degree, mode));

        Assert.InRange(api.MaxConcurrentCalls, 1, degree);
        if (degree > 1) Assert.True(api.MaxConcurrentCalls > 1, "the scan did not run in parallel");
    }

    [Theory]
    [InlineData(ParallelismMode.Files)]
    [InlineData(ParallelismMode.Repositories)]
    public async Task CancellationStopsAParallelScan(ParallelismMode mode)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var api = ManyRepositories();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RepositoryScanner.ScanAsync(api,
            RepositoryScanner.Select(api.Repositories, null), RepositoryScannerTests.Matcher, false,
            ScanParallelism.Create(4, mode), null, cancellation.Token));
    }

    private static Task<ScanResult> ScanAsync(FakeRepositoryProvider api, ScanParallelism parallelism) =>
        RepositoryScanner.ScanAsync(api, RepositoryScanner.Select(api.Repositories, null),
            RepositoryScannerTests.Matcher, collectDiagnostics: true, parallelism, null, CancellationToken.None);

    private static string Serialize(ScanResult result) => JsonSerializer.Serialize(result);

    /// <summary>Ten repositories of six project files each.</summary>
    private static FakeRepositoryProvider ManyRepositories()
    {
        var api = new FakeRepositoryProvider { Latency = Latency };
        for (var r = 0; r < 10; r++)
            api.AddRepository($"repo{r}", files: Enumerable.Range(0, 6).ToDictionary(
                f => $"/src/P{f}/P{f}.csproj",
                f => $"""<Project><ItemGroup><PackageReference Include="Stime.P{f}" Version="1.0.{r}" /></ItemGroup></Project>"""));
        return api;
    }
}
