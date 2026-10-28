using Xunit;

namespace DependencyInventory.Tests;

public sealed class MultiBranchTests
{
    private static readonly DependencyMatcher Matcher = DependencyMatcher.Create(["Serilog"], null);

    private static string Project(string version) =>
        $"""<Project><ItemGroup><PackageReference Include="Serilog" Version="{version}" /></ItemGroup></Project>""";

    private static BranchPolicy All(params string[] branches) => BranchPolicy.FromList(branches).With(BranchMode.All);

    private static Task<ScanResult> ScanAsync(FakeRepositoryProvider api, BranchPolicy branches,
        bool diagnostics = false) =>
        RepositoryScanner.ScanAsync(api, api.Repositories, Matcher, diagnostics, ScanParallelism.Create(1, null),
            null, CancellationToken.None, branches: branches);

    private static FakeRepositoryProvider Api() => new FakeRepositoryProvider()
        .AddRepository("api", files: new Dictionary<string, string> { ["/src/Api.csproj"] = Project("3.0.0") })
        .AddBranch("api", "develop", "develop-sha",
            new Dictionary<string, string> { ["/src/Api.csproj"] = Project("4.0.0") });

    [Fact]
    public async Task EveryListedBranchIsScannedWithItsOwnLines()
    {
        var result = await ScanAsync(Api(), All("main", "develop"));

        Assert.Equal(["api main OK", "api develop OK"],
            result.Coverage.Select(c => $"{c.Repository} {c.Branch} {c.Status}"));
        Assert.Equal(["main api-sha 3.0.0", "develop develop-sha 4.0.0"],
            result.Occurrences.Select(o => $"{o.Branch} {o.Commit} {o.Version}"));
    }

    [Fact]
    public async Task ABranchMatchedBySeveralEntriesIsScannedOnce()
    {
        var result = await ScanAsync(Api(), All("main", "m*", "@default", "*"));

        Assert.Equal(["main", "develop"], result.Coverage.Select(c => c.Branch));
    }

    [Fact]
    public async Task TheFirstModeScansTheFirstBranchOnly()
    {
        var result = await ScanAsync(Api(), BranchPolicy.FromList(["develop", "main"]));

        Assert.Equal(["develop"], result.Coverage.Select(c => c.Branch));
    }

    [Fact]
    public async Task BranchesOnTheSameCommitAreReadOnceAndAllReported()
    {
        var api = Api().AddBranch("api", "release", "api-sha");

        var result = await ScanAsync(api, All("main", "release"), diagnostics: true);

        Assert.Equal(["/src/Api.csproj"], api.FilesRead);
        Assert.Equal(["main api-sha 3.0.0", "release api-sha 3.0.0"],
            result.Occurrences.Select(o => $"{o.Branch} {o.Commit} {o.Version}"));
        Assert.Equal(["main OK", "release OK"], result.Coverage.Select(c => $"{c.Branch} {c.Status}"));
        Assert.Equal(["main api-sha 1", "release api-sha 1"],
            result.Diagnostics.Select(d => $"{d.SelectedBranch} {d.Commit} {d.Files.Count}"));
    }

    [Fact]
    public async Task AFileWithTheSameContentOnTwoCommitsIsReadOnce()
    {
        var api = new FakeRepositoryProvider()
            .AddRepository("api", files: new Dictionary<string, string>
            {
                ["/src/Api.csproj"] = Project("3.0.0"),
                ["/src/Lib.csproj"] = Project("2.0.0")
            })
            .AddBranch("api", "develop", "develop-sha", new Dictionary<string, string>
            {
                ["/src/Api.csproj"] = Project("4.0.0"),
                ["/src/Lib.csproj"] = Project("2.0.0")
            });

        var result = await ScanAsync(api, All("main", "develop"));

        Assert.Equal(["/src/Api.csproj", "/src/Api.csproj", "/src/Lib.csproj"], api.FilesRead.Order());
        Assert.Equal(["main /src/Api.csproj 3.0.0", "main /src/Lib.csproj 2.0.0",
                "develop /src/Api.csproj 4.0.0", "develop /src/Lib.csproj 2.0.0"],
            result.Occurrences.Select(o => $"{o.Branch} {o.File} {o.Version}"));
    }

    [Fact]
    public async Task ABranchFileSectionAppliesItsOwnListInAllMode()
    {
        var api = Api().AddRepository("lib", branch: "master", otherBranches: ["develop", "feature/x"]);
        var policy = BranchPolicy.Parse("main\ndevelop\n[lib]\nmaster\nfeature/*\n", "branches.txt")
            .With(BranchMode.All);

        var result = await ScanAsync(api, policy);

        Assert.Equal(["api main", "api develop", "lib master", "lib feature/x"],
            result.Coverage.Select(c => $"{c.Repository} {c.Branch}"));
    }

    [Fact]
    public async Task ARepositoryWithoutAnyListedBranchIsReportedOnce()
    {
        var result = await ScanAsync(Api(), All("release", "hotfix/*"));

        var coverage = Assert.Single(result.Coverage);
        Assert.Equal(CoverageStatus.NoBranch, coverage.Status);
        Assert.Equal("None of the requested branches exists: release, hotfix/*", coverage.Details);
    }

    [Fact]
    public void AllModeNeedsABranchList()
    {
        Assert.StartsWith("--branch-mode all needs --branch or --branch-file",
            Assert.Throws<ArgumentException>(() => BranchPolicy.FromOptions(null, null, BranchMode.All)).Message);
        Assert.Equal("main, develop (every one that exists)",
            BranchPolicy.FromOptions(["main", "develop"], null, BranchMode.All).Describe());
    }

    [Fact]
    public async Task TheFrameworksAreInventoriedOnEveryBranch()
    {
        var api = new FakeRepositoryProvider()
            .AddRepository("api", files: new Dictionary<string, string>
            {
                ["/src/Api.csproj"] = "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"
            })
            .AddBranch("api", "develop", "develop-sha", new Dictionary<string, string>
            {
                ["/src/Api.csproj"] = "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
            })
            .AddBranch("api", "release", "api-sha");

        var result = await FrameworkScanner.ScanAsync(api, api.Repositories, true, ScanParallelism.Create(1, null),
            null, CancellationToken.None, All("main", "develop", "release"));

        Assert.Equal(["main net8.0", "develop net10.0", "release net8.0"],
            result.Frameworks.Select(f => $"{f.Branch} {f.Framework}"));
        Assert.Equal(["main", "develop", "release"], result.Diagnostics.Select(d => d.SelectedBranch));
    }

    [Fact]
    public async Task AContentReadFailureIsNotKeptForAnotherCommit()
    {
        var inner = new FakeRepositoryProvider()
            .AddRepository("api", files: new Dictionary<string, string> { ["/src/Api.csproj"] = Project("3.0.0") })
            .AddBranch("api", "develop", "develop-sha",
                new Dictionary<string, string> { ["/src/Api.csproj"] = Project("3.0.0") })
            .FailFile("api", "/src/Api.csproj");
        using var api = new ContentReuseProvider(inner);
        await api.GetTreeAsync("api", "api-sha", CancellationToken.None);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.GetFileAsync("api", "api-sha", "/src/Api.csproj", CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.GetFileAsync("api", "api-sha", "/src/Api.csproj", CancellationToken.None));
        Assert.Equal(2, inner.FilesRead.Count);
    }
}
