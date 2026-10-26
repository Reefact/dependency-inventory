using Xunit;

namespace DependencyInventory.Tests;

public sealed class RepositoryScannerTests
{
    private static readonly DependencyMatcher Matcher = DependencyMatcher.Create(["Stime.*", "Serilog"], []);

    private static FakeRepositoryProvider Provider() => new FakeRepositoryProvider()
        .AddRepository("api", files: new Dictionary<string, string>
        {
            ["/Directory.Packages.props"] =
                """<Project><ItemGroup><PackageVersion Include="Stime.Core" Version="3.0.0" /></ItemGroup></Project>""",
            ["/src/Api/Api.csproj"] = """
                <Project><ItemGroup>
                  <PackageReference Include="Stime.Core" />
                  <PackageReference Include="Serilog" Version="4.0.0" />
                  <PackageReference Include="Newtonsoft.Json" Version="13.0.0" />
                </ItemGroup></Project>
                """,
            ["/src/Api/bin/Debug/Api.csproj"] = "<Project />",
            ["/README.md"] = "# api"
        })
        .AddRepository("broken", files: new Dictionary<string, string>
        {
            ["/src/A.csproj"] =
                """<Project><ItemGroup><PackageReference Include="Stime.A" Version="1.0.0" /></ItemGroup></Project>""",
            ["/src/B.csproj"] = "<not xml",
            ["/src/C.csproj"] = "<Project />"
        })
        .FailFile("broken", "/src/C.csproj")
        .AddRepository("empty", files: new Dictionary<string, string> { ["/README.md"] = "x" })
        .AddRepository("nobranch", branch: null)
        .AddRepository("old", disabled: true)
        .AddRepository("offline")
        .FailBranches("offline");

    private static Task<RepositoryScanResult> ScanAsync(FakeRepositoryProvider api, string name,
        RepositoryDiagnostic? diagnostic = null) =>
        RepositoryScanner.ScanRepositoryAsync(api, api.Repositories.Single(r => r.Name == name),
            Matcher, diagnostic, CancellationToken.None);

    [Theory]
    [InlineData("api", CoverageStatus.OK)]
    [InlineData("broken", CoverageStatus.Partial)]
    [InlineData("empty", CoverageStatus.NoNuGetFiles)]
    [InlineData("nobranch", CoverageStatus.NoBranch)]
    [InlineData("old", CoverageStatus.Disabled)]
    [InlineData("offline", CoverageStatus.Failed)]
    public async Task CoverageStatusReflectsWhatCouldBeRead(string repository, CoverageStatus expected)
    {
        var result = await ScanAsync(Provider(), repository);
        Assert.Equal(expected, result.Coverage.Status);
    }

    [Fact]
    public async Task MatchingReferencesAreReportedWithTheirCentralVersion()
    {
        var result = await ScanAsync(Provider(), "api");

        Assert.Equal(2, result.Coverage.FilesExamined);
        Assert.Equal(2, result.Coverage.References);
        var core = Assert.Single(result.Occurrences,
            x => x.Package == "Stime.Core" && x.Kind == PackageKind.Reference);
        Assert.Equal("3.0.0", core.Version);
        Assert.Equal(VersionSource.Central, core.VersionSource);
        Assert.Equal("api-sha", core.Commit);
        Assert.Contains(result.Occurrences, x => x.Kind == PackageKind.CentralVersion);
        Assert.DoesNotContain(result.Occurrences, x => x.Package == "Newtonsoft.Json");
        Assert.DoesNotContain(result.Occurrences, x => x.File.Contains("/bin/"));
    }

    [Fact]
    public async Task UnreadableFilesMakeTheRepositoryPartialAndAreListed()
    {
        var result = await ScanAsync(Provider(), "broken");

        Assert.Equal(1, result.Coverage.FilesExamined);
        Assert.Contains("/src/B.csproj", result.Coverage.Details);
        Assert.Contains("/src/C.csproj", result.Coverage.Details);
        Assert.Single(result.Occurrences);
    }

    [Fact]
    public async Task ATimeoutMakesTheRepositoryPartialInsteadOfStoppingTheScan()
    {
        var api = Provider().FailFile("api", "/src/Api/Api.csproj",
            new TaskCanceledException("timed out", new TimeoutException()));

        var result = await ScanAsync(api, "api");

        Assert.Equal(CoverageStatus.Partial, result.Coverage.Status);
        Assert.Contains("/src/Api/Api.csproj: timed out", result.Coverage.Details);
    }

    [Fact]
    public async Task CancellationByTheUserStopsTheScan()
    {
        using var cancellation = new CancellationTokenSource();
        var api = Provider();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RepositoryScanner.ScanRepositoryAsync(
            api, api.Repositories.Single(r => r.Name == "api"), Matcher, null, cancellation.Token));
    }

    [Fact]
    public async Task DiagnosticRecordsBranchesAndEveryFileExamined()
    {
        var diagnostic = new RepositoryDiagnostic { Project = "project", Repository = "broken" };
        await ScanAsync(Provider(), "broken", diagnostic);

        Assert.Equal("main", diagnostic.SelectedBranch);
        Assert.Equal("broken-sha", diagnostic.Commit);
        Assert.Equal(["main"], diagnostic.AvailableBranches);
        Assert.Equal(3, diagnostic.Files.Count);
        Assert.Equal(2, diagnostic.Files.Count(f => f.Error is not null));
    }

    [Fact]
    public void SelectFiltersByNameIgnoringCaseAndOrdersByName()
    {
        var repositories = Provider().Repositories;

        Assert.Equal(["api", "broken", "empty", "nobranch", "offline", "old"],
            RepositoryScanner.Select(repositories, null).Select(r => r.Name));
        Assert.Equal("api", Assert.Single(RepositoryScanner.Select(repositories, "API")).Name);
        Assert.Empty(RepositoryScanner.Select(repositories, "missing"));
    }

    [Fact]
    public async Task ScanAggregatesRepositoriesAndReportsProgress()
    {
        var api = Provider();
        var progress = new RecordingProgress();

        var result = await RepositoryScanner.ScanAsync(api, RepositoryScanner.Select(api.Repositories, null),
            Matcher, collectDiagnostics: true, progress, CancellationToken.None);

        Assert.Equal(6, result.Coverage.Count);
        Assert.Equal(6, result.Diagnostics.Count);
        Assert.Equal(2, result.IncompleteRepositories);
        Assert.Equal(4, result.Occurrences.Count);
        Assert.Equal(["start api", "done api", "start broken", "done broken"], progress.Events.Take(4));
        Assert.Equal(12, progress.Events.Count);
    }

    [Fact]
    public async Task DiagnosticsAreCollectedOnlyWhenRequested()
    {
        var api = Provider();
        var result = await RepositoryScanner.ScanAsync(api, api.Repositories, Matcher,
            collectDiagnostics: false, progress: null, CancellationToken.None);

        Assert.Empty(result.Diagnostics);
    }

    private sealed class RecordingProgress : IScanProgress
    {
        public List<string> Events { get; } = [];
        public void RepositoryStarted(Repository repository) => Events.Add($"start {repository.Name}");
        public void RepositoryCompleted(Repository repository) => Events.Add($"done {repository.Name}");
    }
}
