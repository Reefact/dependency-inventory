using System.Text.Json;
using Xunit;

namespace DependencyInventory.Tests;

public sealed class CommitDateTests
{
    private static readonly DependencyMatcher Matcher = DependencyMatcher.Create(["Serilog"], null);

    private static FakeRepositoryProvider Api() => new FakeRepositoryProvider()
        .AddRepository("api", files: new Dictionary<string, string>
        {
            ["/src/Api.csproj"] =
                """<Project><ItemGroup><PackageReference Include="Serilog" Version="3.0.0" /></ItemGroup></Project>"""
        })
        .AddRepository("old", disabled: true)
        .AddRepository("empty", branch: null);

    [Theory]
    [InlineData("""{"committer": {"name": "a", "date": "2026-10-28T11:42:56+02:00"}}""", "2026-10-28T09:42:56Z")]
    [InlineData("""{"committer": {"name": "a"}}""", null)]
    [InlineData("""{"commitId": "abc"}""", null)]
    public void AzureDevOpsGivesTheCommitterDate(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, AzureDevOpsClient.CommitDate(document.RootElement)?.ToString("yyyy-MM-ddTHH:mm:ssZ"));
    }

    [Theory]
    [InlineData("""{"authored_date": "2026-01-01T00:00:00Z", "committed_date": "2026-10-28T09:42:56.000Z"}""",
        "2026-10-28T09:42:56Z")]
    [InlineData("""{"committed_date": null}""", null)]
    public void GitLabGivesTheCommitterDate(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected,
            GitLabRepositoryProvider.CommitDate(document.RootElement)?.ToString("yyyy-MM-ddTHH:mm:ssZ"));
    }

    [Fact]
    public async Task ThePackageInventoryGivesTheDateOfEveryScannedCommit()
    {
        var api = Api();

        var result = await RepositoryScanner.ScanAsync(api, api.Repositories, Matcher, true,
            ScanParallelism.Sequential, null, CancellationToken.None);

        Assert.Equal([FakeRepositoryProvider.CommitDate, null, null], result.Coverage.Select(c => c.CommitDate));
        Assert.Equal([CoverageStatus.OK, CoverageStatus.Disabled, CoverageStatus.NoBranch],
            result.Coverage.Select(c => c.Status));
        Assert.Equal(FakeRepositoryProvider.CommitDate, result.Diagnostics[0].CommitDate);
    }

    [Fact]
    public async Task EveryRepositoryGivesItsAddressAndTheScannedCommit()
    {
        var api = Api();

        var packages = await RepositoryScanner.ScanAsync(api, api.Repositories, Matcher, false,
            ScanParallelism.Sequential, null, CancellationToken.None);
        var frameworks = await FrameworkScanner.ScanAsync(api, api.Repositories, false, ScanParallelism.Sequential,
            null, CancellationToken.None);

        string?[] commits = ["api-sha", null, null];
        string?[] urls = ["https://example/api", "https://example/old", "https://example/empty"];
        Assert.Equal(commits, packages.Coverage.Select(c => c.Commit));
        Assert.Equal(urls, packages.Coverage.Select(c => c.Url));
        Assert.Equal(commits, frameworks.Coverage.Select(c => c.Commit));
        Assert.Equal(urls, frameworks.Coverage.Select(c => c.Url));
    }

    [Fact]
    public async Task TheFrameworksInventoryGivesTheDateOfEveryScannedCommit()
    {
        var api = Api();

        var result = await FrameworkScanner.ScanAsync(api, api.Repositories, false, ScanParallelism.Sequential,
            null, CancellationToken.None);

        Assert.Equal([FakeRepositoryProvider.CommitDate, null, null], result.Coverage.Select(c => c.CommitDate));
    }

    [Fact]
    public async Task ADateThatCannotBeReadIsExplainedWithoutChangingTheStatus()
    {
        var api = Api().FailCommitDate("api-sha");

        var coverage = (await RepositoryScanner.ScanRepositoryAsync(api, api.Repositories[0], Matcher, null,
            CancellationToken.None)).Coverage;

        Assert.Equal((CoverageStatus.OK, null, "Commit date not read: commit unavailable"),
            (coverage.Status, coverage.CommitDate, coverage.Details));
        Assert.Equal(1, coverage.References);
    }

    [Fact]
    public async Task BranchesOnTheSameCommitReadTheDateOnce()
    {
        var api = Api().AddBranch("api", "release", "api-sha");

        var result = await RepositoryScanner.ScanAsync(api, [api.Repositories[0]], Matcher, false,
            ScanParallelism.Sequential, null, CancellationToken.None,
            branches: BranchPolicy.FromList(["main", "release"]).With(BranchMode.All));

        Assert.Equal(["api-sha"], api.CommitDatesRead);
        Assert.Equal([FakeRepositoryProvider.CommitDate, FakeRepositoryProvider.CommitDate],
            result.Coverage.Select(c => c.CommitDate));
    }

    [Fact]
    public async Task ThePackageReportWritesTheDateInUtc()
    {
        var report = new ScanReport(DateTimeOffset.UnixEpoch, "https://dev.azure.com/acme/project", [], [],
        [
            new("project", "api", "main", CoverageStatus.OK, 1, 0, null,
                new DateTimeOffset(2026, 10, 28, 11, 42, 56, TimeSpan.FromHours(2)), "abc", "https://example/api"),
            new("project", "old", null, CoverageStatus.Disabled, 0, 0, "Repository disabled", Url: "https://example/old")
        ]);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await ReportWriter.SaveAsync(report, directory, false, CancellationToken.None);

            Assert.Equal(
            [
                "Scope,Repository,Branch,Commit,CommitDate,Status,FilesExamined,References,Details,Url",
                "project,api,main,abc,2026-10-28T09:42:56Z,OK,1,0,,https://example/api",
                "project,old,,,,Disabled,0,0,Repository disabled,https://example/old"
            ], await File.ReadAllLinesAsync(Path.Combine(directory, "repositories.csv")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
