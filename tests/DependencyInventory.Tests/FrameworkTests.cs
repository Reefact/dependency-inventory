using Xunit;

namespace DependencyInventory.Tests;

public sealed class FrameworkTests
{
    private static string Properties(string body) => $"<Project><PropertyGroup>{body}</PropertyGroup></Project>";

    [Theory]
    [InlineData("<TargetFramework>net8.0</TargetFramework>", "net8.0", false)]
    [InlineData("<TargetFrameworks>net8.0; netstandard2.0;;NET8.0</TargetFrameworks>", "net8.0,netstandard2.0", false)]
    [InlineData("<TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>", "net472", false)]
    [InlineData("<TargetFrameworkVersion>v3.5</TargetFrameworkVersion>", "net35", false)]
    [InlineData("<TargetFramework>$(DefaultTfm)</TargetFramework>", "", true)]
    [InlineData("<TargetFrameworks>$(Libraries);net48</TargetFrameworks>", "net48", true)]
    [InlineData("<TargetFrameworkVersion>$(Version)</TargetFrameworkVersion>", "", true)]
    [InlineData("<OutputType>Exe</OutputType>", "", false)]
    public void ParseReadsEveryDeclaration(string body, string frameworks, bool unresolved)
    {
        var declared = FrameworkParser.Parse("/src/Api.csproj", Properties(body));

        Assert.Equal(frameworks, string.Join(",", declared.Frameworks));
        Assert.Equal(unresolved, declared.Unresolved);
    }

    [Fact]
    public void ParseKeepsConditionalDeclarations()
    {
        var declared = FrameworkParser.Parse("/src/Api.csproj", """
            <Project>
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <PropertyGroup Condition="'$(OS)' == 'Windows_NT'"><TargetFramework>net48</TargetFramework></PropertyGroup>
            </Project>
            """);

        Assert.Equal(["net8.0", "net48"], declared.Frameworks);
    }

    [Theory]
    [InlineData("/src/Api/Api.csproj", true)]
    [InlineData("/src/Legacy/Legacy.vbproj", true)]
    [InlineData("/Directory.Build.props", true)]
    [InlineData("/src/directory.build.props", true)]
    [InlineData("/Directory.Packages.props", false)]
    [InlineData("/Directory.Build.targets", false)]
    [InlineData("/src/packages.config", false)]
    [InlineData("/src/Api/obj/Api.csproj", false)]
    public void IsInterestingFileKeepsProjectsAndBuildProps(string path, bool expected)
    {
        Assert.Equal(expected, FrameworkParser.IsInterestingFile(path));
    }

    private static readonly DeclaredFrameworks Net8 = new(["net8.0"], false);
    private static readonly DeclaredFrameworks Net10 = new(["net10.0"], false);

    [Fact]
    public void ResolvePrefersTheProjectOwnDeclaration()
    {
        var files = new Dictionary<string, DeclaredFrameworks?>
        {
            ["/Directory.Build.props"] = Net8,
            ["/src/Api/Api.csproj"] = Net10
        };

        Assert.Equal([new ResolvedFramework("net10.0", FrameworkSource.Project)],
            FrameworkParser.Resolve("/src/Api/Api.csproj", files));
    }

    [Fact]
    public void ResolveInheritsFromTheNearestDirectoryBuildPropsOnly()
    {
        var files = new Dictionary<string, DeclaredFrameworks?>
        {
            ["/Directory.Build.props"] = Net8,
            ["/src/Directory.Build.props"] = Net10,
            ["/src/Api/Api.csproj"] = DeclaredFrameworks.None,
            ["/tests/Api.Tests/Api.Tests.csproj"] = DeclaredFrameworks.None
        };

        Assert.Equal([new ResolvedFramework("net10.0", FrameworkSource.DirectoryBuildProps)],
            FrameworkParser.Resolve("/src/Api/Api.csproj", files));
        Assert.Equal([new ResolvedFramework("net8.0", FrameworkSource.DirectoryBuildProps)],
            FrameworkParser.Resolve("/tests/Api.Tests/Api.Tests.csproj", files));
    }

    [Fact]
    public void ResolveDoesNotSkipANearestBuildPropsThatDeclaresNothingOrCannotBeRead()
    {
        var files = new Dictionary<string, DeclaredFrameworks?>
        {
            ["/Directory.Build.props"] = Net8,
            ["/src/Directory.Build.props"] = DeclaredFrameworks.None,
            ["/lib/Directory.Build.props"] = null,
            ["/src/Api/Api.csproj"] = DeclaredFrameworks.None,
            ["/lib/Lib/Lib.csproj"] = DeclaredFrameworks.None
        };

        Assert.Equal([new ResolvedFramework(null, FrameworkSource.Unresolved)],
            FrameworkParser.Resolve("/src/Api/Api.csproj", files));
        Assert.Equal([new ResolvedFramework(null, FrameworkSource.Unresolved)],
            FrameworkParser.Resolve("/lib/Lib/Lib.csproj", files));
    }

    [Fact]
    public void ResolveReportsAPartlyUnresolvedDeclaration()
    {
        var files = new Dictionary<string, DeclaredFrameworks?>
        {
            ["/src/Api/Api.csproj"] = new DeclaredFrameworks(["net48"], true)
        };

        Assert.Equal(
            [new ResolvedFramework("net48", FrameworkSource.Project), new ResolvedFramework(null, FrameworkSource.Unresolved)],
            FrameworkParser.Resolve("/src/Api/Api.csproj", files));
    }

    private static FakeRepositoryProvider Provider() =>
        new FakeRepositoryProvider()
        .AddRepository("api", files: new Dictionary<string, string>
        {
            ["/Directory.Build.props"] = Properties("<TargetFramework>net8.0</TargetFramework>"),
            ["/src/Api/Api.csproj"] = Properties("<TargetFrameworks>net8.0;net10.0</TargetFrameworks>"),
            ["/src/Core/Core.csproj"] = "<Project />",
            ["/src/Legacy/Legacy.csproj"] =
                Properties("<TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>"),
            ["/src/Api/bin/Api.csproj"] = "<Project />",
            ["/README.md"] = "# api"
        })
        .AddRepository("broken", files: new Dictionary<string, string>
        {
            ["/src/A.csproj"] = Properties("<TargetFramework>net6.0</TargetFramework>"),
            ["/src/B.csproj"] = "<not xml"
        })
        .AddRepository("scripts", files: new Dictionary<string, string> { ["/Directory.Build.props"] = "<Project />" })
        .AddRepository("nobranch", branch: null)
        .AddRepository("old", disabled: true);

    private static Task<FrameworkRepositoryResult> ScanAsync(FakeRepositoryProvider api, string name,
        FrameworkRepositoryDiagnostic? diagnostic = null) =>
        FrameworkScanner.ScanRepositoryAsync(api, api.Repositories.Single(r => r.Name == name), diagnostic,
            CancellationToken.None);

    [Fact]
    public async Task EveryProjectIsListedWithItsFrameworks()
    {
        var result = await ScanAsync(Provider(), "api");

        Assert.Equal(
        [
            "/src/Api/Api.csproj net8.0 Project",
            "/src/Api/Api.csproj net10.0 Project",
            "/src/Core/Core.csproj net8.0 DirectoryBuildProps",
            "/src/Legacy/Legacy.csproj net472 Project"
        ], result.Frameworks.Select(x => $"{x.ProjectFile} {x.Framework} {x.FrameworkSource}"));
        Assert.All(result.Frameworks, x => Assert.Equal("api-sha", x.Commit));
        Assert.Equal(CoverageStatus.OK, result.Coverage.Status);
        Assert.Equal(4, result.Coverage.FilesExamined);
        Assert.Equal(3, result.Coverage.Projects);
    }

    [Fact]
    public async Task PathsDifferingOnlyByCaseAreTwoProjects()
    {
        var api = new FakeRepositoryProvider().AddRepository("cased", files: new Dictionary<string, string>
        {
            ["/src/App/App.csproj"] = Properties("<TargetFramework>net8.0</TargetFramework>"),
            ["/src/app/App.csproj"] = Properties("<TargetFramework>net48</TargetFramework>")
        });

        var result = await ScanAsync(api, "cased");

        Assert.Equal(["/src/App/App.csproj net8.0", "/src/app/App.csproj net48"],
            result.Frameworks.Select(x => $"{x.ProjectFile} {x.Framework}").Order(StringComparer.Ordinal));
        Assert.Equal(2, result.Coverage.Projects);
    }

    [Theory]
    [InlineData("broken", CoverageStatus.Partial)]
    [InlineData("scripts", CoverageStatus.NoProjectFiles)]
    [InlineData("nobranch", CoverageStatus.NoBranch)]
    [InlineData("old", CoverageStatus.Disabled)]
    public async Task CoverageStatusReflectsWhatCouldBeRead(string repository, CoverageStatus expected)
    {
        var result = await ScanAsync(Provider(), repository);

        Assert.Equal(expected, result.Coverage.Status);
    }

    [Fact]
    public async Task UnreadableProjectsAreReportedInTheCoverage()
    {
        var result = await ScanAsync(Provider(), "broken");

        var only = Assert.Single(result.Frameworks);
        Assert.Equal("net6.0", only.Framework);
        Assert.StartsWith("/src/B.csproj: ", result.Coverage.Details);
    }

    [Fact]
    public async Task DiagnosticRecordsBranchesAndEveryFileExamined()
    {
        var diagnostic = new FrameworkRepositoryDiagnostic { Project = "project", Repository = "broken" };
        await ScanAsync(Provider(), "broken", diagnostic);

        Assert.Equal("main", diagnostic.SelectedBranch);
        Assert.Equal("broken-sha", diagnostic.Commit);
        Assert.Equal(["main"], diagnostic.AvailableBranches);
        Assert.Equal(["/src/A.csproj", "/src/B.csproj"], diagnostic.Files.Select(f => f.Path));
        Assert.NotNull(diagnostic.Files[1].Error);
    }

    [Fact]
    public async Task ScanAggregatesRepositoriesInOrder()
    {
        var api = Provider();
        var result = await FrameworkScanner.ScanAsync(api, RepositoryScanner.Select(api.Repositories, null),
            collectDiagnostics: false, ScanParallelism.Sequential, progress: null, CancellationToken.None);

        Assert.Equal(["api", "broken", "nobranch", "old", "scripts"], result.Coverage.Select(x => x.Repository));
        Assert.Equal(5, result.Frameworks.Count);
        Assert.Equal(1, result.IncompleteRepositories);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task ReportsWriteFrameworksAndCoverage()
    {
        var report = new FrameworkReport(DateTimeOffset.UnixEpoch, "https://dev.azure.com/acme/project",
            [
                new("project", "api", "main", "abc", "/src/Api.csproj", "net8.0", FrameworkSource.DirectoryBuildProps,
                    "https://example/api"),
                new("project", "api", "main", "abc", "/src/Lib.csproj", null, FrameworkSource.Unresolved,
                    "https://example/lib")
            ],
            [new("project", "api", "main", CoverageStatus.NoProjectFiles, 3, 2, null)]);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await ReportWriter.SaveFrameworksAsync(report, directory, CancellationToken.None);

            var frameworks = await File.ReadAllLinesAsync(Path.Combine(directory, "frameworks.csv"));
            Assert.Equal(
            [
                "Project;Repository;Branch;Commit;ProjectFile;Framework;FrameworkSource;Url",
                "project;api;main;abc;/src/Api.csproj;net8.0;DirectoryBuildProps;https://example/api",
                "project;api;main;abc;/src/Lib.csproj;;Unresolved;https://example/lib"
            ], frameworks);
            var repositories = await File.ReadAllLinesAsync(Path.Combine(directory, "repositories.csv"));
            Assert.Equal("Project;Repository;Branch;Status;FilesExamined;Projects;Details", repositories[0]);
            Assert.Equal("project;api;main;NoProjectFiles;3;2;", repositories[1]);
            var json = await File.ReadAllTextAsync(Path.Combine(directory, "report.json"));
            Assert.Contains("\"FrameworkSource\": \"DirectoryBuildProps\"", json);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
