using Xunit;

namespace DependencyInventory.Tests;

public sealed class TransitiveTests
{
    /// <summary>
    /// A depends on B (a range whose minimum is not published), C, and Legacy for net462 only; B depends back on A;
    /// C depends on D, declared by the project, on F, and on a package found nowhere; F asks for E 3.0.0, deeper than
    /// B's E 1.0.0, on another branch.
    /// </summary>
    private static FakePackageFeed NuGetOrg() => new FakePackageFeed { Origin = "https://api.nuget.org/v3/index.json" }
        .Package("A", "1.0.0", """
            <group targetFramework="net8.0">
              <dependency id="B" version="[1.0.0, )" /><dependency id="C" version="1.0.0" />
            </group>
            <group targetFramework="net462"><dependency id="Legacy" version="1.0.0" /></group>
            """)
        .Package("B", "1.2.0", """<dependency id="E" version="1.0.0" /><dependency id="A" version="1.0.0" />""")
        .Package("B", "2.0.0")
        .Package("C", "1.0.0", """
            <dependency id="D" version="2.0.0" /><dependency id="F" version="1.0.0" />
            <dependency id="Missing" version="1.0.0" />
            """)
        .Package("D", "1.0.0")
        .Package("D", "2.0.0")
        .Package("F", "1.0.0", """<dependency id="E" version="3.0.0" />""")
        .Package("E", "1.0.0", types: "namespace Contoso.E { public class Thing { } }")
        .Package("E", "3.0.0", types: "namespace Contoso.E { public class Thing { } }")
        .Package("E", "4.0.0", types: "namespace Contoso.E { public class Thing { } }")
        .Package("Legacy", "1.0.0");

    private static PackageCatalog Catalog(FakePackageFeed? feed = null) =>
        new([new PackageSourceEntry(feed ?? NuGetOrg(), PackageSource.NuGetOrg, null)]);

    private static string Project(string frameworks, params string[] references) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFrameworks>{frameworks}</TargetFrameworks></PropertyGroup>
          <ItemGroup>
            {string.Concat(references.Select(r => r.Contains('@')
                ? $"""<PackageReference Include="{r.Split('@')[0]}" Version="{r.Split('@')[1]}" />"""
                : $"""<PackageReference Include="{r}" />"""))}
          </ItemGroup>
        </Project>
        """;

    private static async Task<(RepositoryScanResult Result, RepositoryDiagnostic Diagnostic)> ScanAsync(
        Dictionary<string, string> files, string pattern = "*", PackageCatalog? catalog = null)
    {
        var api = new FakeRepositoryProvider().AddRepository("app", files: files);
        var diagnostic = new RepositoryDiagnostic { Project = "project", Repository = "app" };
        catalog ??= Catalog();
        var result = await RepositoryScanner.ScanRepositoryAsync(api, api.Repositories.Single(),
            DependencyMatcher.Create([pattern], []), diagnostic, CancellationToken.None,
            usage: new PackageUsageAnalyzer(catalog, true), transitive: new TransitiveResolver(catalog));
        return (result, diagnostic);
    }

    private static string[] Rows(RepositoryScanResult result) => result.TransitiveDependencies!
        .Select(t => $"{t.Framework} {t.Package} {t.Version} {t.Via} {t.Resolution}").ToArray();

    [Fact]
    public async Task TheGraphIsComputedFollowingTheNuGetRules()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = Project("net8.0", "A@1.0.0", "D@1.0.0")
        });

        // B: the lowest version satisfying [1.0.0, ); D: declared, so it wins over C's 2.0.0 and is never transitive;
        // E: cousin dependencies, the highest minimum wins whatever the depth, reached by its shortest path;
        // A: the cycle stops; Legacy: net462 only.
        Assert.Equal(
        [
            "net8.0 B 1.2.0 A > B Computed",
            "net8.0 C 1.0.0 A > C Computed",
            "net8.0 E 3.0.0 A > B > E Computed",
            "net8.0 F 1.0.0 A > C > F Computed"
        ], Rows(result));
    }

    [Fact]
    public async Task EachTargetFrameworkHasItsOwnGraph()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = Project("net462;net8.0", "A@1.0.0")
        });

        Assert.Equal(["net462 Legacy 1.0.0 A > Legacy Computed"],
            Rows(result).Where(r => r.StartsWith("net462")));
        Assert.DoesNotContain(Rows(result), r => r.StartsWith("net8.0 Legacy"));
    }

    [Fact]
    public async Task AnEmptyDependencyGroupMeansNoDependencyForItsFrameworks()
    {
        // As xunit.assert: net6.0 and later depend on nothing, netstandard1.1 on NETStandard.Library.
        var feed = new FakePackageFeed { Origin = "https://api.nuget.org/v3/index.json" }
            .Package("Assertions", "1.0.0", """
                <group targetFramework="net6.0" />
                <group targetFramework=".NETStandard1.1"><dependency id="NETStandard.Library" version="1.6.1" /></group>
                """)
            .Package("NETStandard.Library", "1.6.1");

        var (result, _) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = Project("net8.0;netstandard1.3", "Assertions@1.0.0")
        }, catalog: Catalog(feed));

        Assert.Equal(["netstandard1.3 NETStandard.Library 1.6.1 Assertions > NETStandard.Library Computed"],
            Rows(result));
    }

    [Fact]
    public async Task ABranchThatCannotBeFollowedIsInTheDiagnostics()
    {
        var (_, diagnostic) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = Project("net8.0", "A@1.0.0", "D@1.0.0", "Unknown@$(Version)")
        });

        var graph = Assert.Single(diagnostic.Transitive);
        Assert.Equal(("/App/App.csproj", "net8.0", DependencyResolution.Computed, 4),
            (graph.File, graph.Framework, graph.Resolution, graph.Packages));
        Assert.Equal(
        [
            "Unknown: the declared version '$(Version)' is neither a version nor a version range.",
            "A > C > Missing (>= 1.0.0): No version satisfying (>= 1.0.0) in nuget.org"
        ], graph.Unresolved);
    }

    [Fact]
    public async Task TransitivePinningAppliesTheCentralVersions()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/Directory.Packages.props"] = """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="A" Version="1.0.0" />
                    <PackageVersion Include="E" Version="4.0.0" />
                  </ItemGroup>
                </Project>
                """,
            ["/App/App.csproj"] = Project("net8.0", "A")
        });

        Assert.Contains("net8.0 E 4.0.0 A > B > E Computed", Rows(result));
    }

    [Fact]
    public async Task ACentralVersionPinnedBelowWhatTheGraphAsksIsReported()
    {
        var (result, diagnostic) = await ScanAsync(new()
        {
            ["/Directory.Packages.props"] = """
                <Project>
                  <PropertyGroup>
                    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="A" Version="1.0.0" /><PackageVersion Include="E" Version="1.0.0" />
                  </ItemGroup>
                </Project>
                """,
            ["/App/App.csproj"] = Project("net8.0", "A")
        });

        Assert.Contains("net8.0 E 1.0.0 A > B > E Computed", Rows(result));
        Assert.Contains("A > B > E: the central version 1.0.0 is pinned below the (>= 3.0.0) asked; " +
            "NuGet refuses this restore (NU1109).", Assert.Single(diagnostic.Transitive).Unresolved);
    }

    [Theory]
    [InlineData("3.0.0", null)]
    [InlineData("2.0.0", "A > G: the central version 2.0.0 is pinned above the (>= 1.0.0 && < 2.0.0) asked; " +
        "NuGet warns of it (NU1608) and restores it.")]
    [InlineData("1.0.0", "A > G: the central version 1.0.0 is pinned below the (> 1.0.0 && < 2.0.0) asked; " +
        "NuGet refuses this restore (NU1109).")]
    public async Task APinnedVersionOutsideTheAskedRangesIsReportedAsNuGetWould(string pin, string? issue)
    {
        // A asks for G (1.0.0, 2.0.0) and [1.0.0, 2.0.0): 2.0.0 is above both, 1.0.0 below the first one only.
        var feed = new FakePackageFeed { Origin = "https://api.nuget.org/v3/index.json" }
            .Package("A", "1.0.0", """<dependency id="G" version="(1.0.0, 2.0.0)" /><dependency id="H" version="1.0.0" />""")
            .Package("H", "1.0.0", """<dependency id="G" version="[1.0.0, 2.0.0)" />""")
            .Package("G", "1.0.0").Package("G", "1.5.0").Package("G", "2.0.0").Package("G", "3.0.0");
        var (result, diagnostic) = await ScanAsync(new()
        {
            ["/Directory.Packages.props"] = $"""
                <Project>
                  <PropertyGroup>
                    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="A" Version="1.0.0" /><PackageVersion Include="G" Version="{pin}" />
                  </ItemGroup>
                </Project>
                """,
            ["/App/App.csproj"] = Project("net8.0", "A")
        }, catalog: Catalog(feed));

        Assert.Contains($"net8.0 G {pin} A > G Computed", Rows(result));
        var unresolved = Assert.Single(diagnostic.Transitive).Unresolved;
        if (pin == "3.0.0")
            Assert.Equal(2, unresolved.Count(u => u.Contains("NU1608")));
        else
            Assert.Contains(issue!, unresolved);
    }

    [Theory]
    [InlineData("<TargetFramework>$(TargetNet)</TargetFramework>",
        "Target framework not resolved: the dependencies of every group of the packages are merged, so packages of " +
        "a framework the project does not target may be listed.")]
    [InlineData("<TargetFrameworks>net8.0;$(Extra)</TargetFrameworks>",
        "Some target frameworks are not resolved (an MSBuild property): only those listed are computed.")]
    public async Task AnUnresolvedTargetFrameworkIsReported(string frameworks, string note)
    {
        var (result, diagnostic) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>{frameworks}</PropertyGroup>
                  <ItemGroup><PackageReference Include="A" Version="1.0.0" /><PackageReference Include="D" Version="1.0.0" /></ItemGroup>
                </Project>
                """
        });

        Assert.Equal(note, Assert.Single(diagnostic.Transitive).Unresolved[0]);
        // Without framework, the net462 group of A is merged with the net8.0 one.
        Assert.Equal(frameworks.Contains("$(TargetNet)"),
            result.TransitiveDependencies!.Any(t => t.Package == "Legacy"));
    }

    [Fact]
    public async Task AProjectCanDisableTheTransitivePinningItInherits()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/Directory.Packages.props"] = """
                <Project>
                  <PropertyGroup>
                    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="A" Version="1.0.0" /><PackageVersion Include="E" Version="4.0.0" />
                  </ItemGroup>
                </Project>
                """,
            ["/App/App.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
                  </PropertyGroup>
                  <ItemGroup><PackageReference Include="A" /></ItemGroup>
                </Project>
                """
        });

        Assert.Contains("net8.0 E 3.0.0 A > B > E Computed", Rows(result));
    }

    [Fact]
    public async Task ANearerDirectoryBuildPropsShadowsTheReferencesOfThoseAboveIt()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/Directory.Build.props"] =
                """<Project><ItemGroup><PackageReference Include="E" Version="1.0.0" /></ItemGroup></Project>""",
            ["/src/Directory.Build.props"] = "<Project />",
            ["/src/App/App.csproj"] = Project("net8.0", "A@1.0.0", "D@1.0.0")
        });

        // E is not declared for App: it is reached through A.
        Assert.Contains("net8.0 E 3.0.0 A > B > E Computed", Rows(result));
    }

    [Fact]
    public async Task ALockFileIsReadEvenWithoutAnyDeclaredReference()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = Project("netstandard2.0"),
            ["/App/packages.lock.json"] = """
                {
                  "version": 1,
                  "dependencies": {
                    ".NETStandard,Version=v2.0": {
                      "NETStandard.Library": { "type": "Direct", "resolved": "2.0.3",
                        "dependencies": { "Microsoft.NETCore.Platforms": "1.1.0" } },
                      "Microsoft.NETCore.Platforms": { "type": "Transitive", "resolved": "1.1.0" }
                    }
                  }
                }
                """
        }, catalog: new PackageCatalog([]));

        Assert.Equal(
            ["netstandard2.0 Microsoft.NETCore.Platforms 1.1.0 NETStandard.Library > Microsoft.NETCore.Platforms LockFile"],
            Rows(result));
    }

    [Fact]
    public async Task WithoutPinningCentralVersionsDoNotApplyToTransitivePackages()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/Directory.Packages.props"] = """
                <Project><ItemGroup>
                  <PackageVersion Include="A" Version="1.0.0" /><PackageVersion Include="E" Version="3.0.0" />
                </ItemGroup></Project>
                """,
            ["/App/App.csproj"] = Project("net8.0", "A")
        });

        Assert.Contains("net8.0 E 3.0.0 A > B > E Computed", Rows(result));
    }

    [Fact]
    public async Task ALockFileIsReadAsIs()
    {
        var (result, diagnostic) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = Project("net8.0", "A@1.0.0"),
            ["/App/packages.lock.json"] = """
                {
                  "version": 1,
                  "dependencies": {
                    ".NETCoreApp,Version=v8.0": {
                      "A": { "type": "Direct", "requested": "[1.0.0, )", "resolved": "1.0.0", "dependencies": { "B": "1.0.0" } },
                      "B": { "type": "Transitive", "resolved": "1.5.0", "dependencies": { "E": "1.0.0" } },
                      "E": { "type": "Transitive", "resolved": "1.0.0" },
                      "Lib": { "type": "Project", "dependencies": { "Z": "[2.0.0, )" } },
                      "Z": { "type": "CentralTransitive", "requested": "[2.0.0, )", "resolved": "2.0.0" }
                    },
                    ".NETCoreApp,Version=v8.0/win-x64": {
                      "Native": { "type": "Transitive", "resolved": "1.0.0" }
                    }
                  }
                }
                """
        }, catalog: new PackageCatalog([]));

        Assert.Equal(
        [
            "net8.0 B 1.5.0 A > B LockFile",
            "net8.0 E 1.0.0 A > B > E LockFile",
            "net8.0 Z 2.0.0 Lib > Z LockFile"
        ], Rows(result));
        Assert.All(diagnostic.Transitive, g => Assert.Empty(g.Unresolved));
    }

    [Fact]
    public async Task AnUnreadableLockFileFallsBackToTheComputedGraph()
    {
        var (result, diagnostic) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = Project("net8.0", "A@1.0.0", "D@1.0.0"),
            ["/App/packages.lock.json"] = "{ not json"
        });

        Assert.All(result.TransitiveDependencies!, t => Assert.Equal(DependencyResolution.Computed, t.Resolution));
        Assert.StartsWith("packages.lock.json not read, the graph is computed:",
            Assert.Single(diagnostic.Transitive).Unresolved[0]);
    }

    [Fact]
    public async Task FiltersMatchPackagesOnlyReachedTransitively()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = Project("net8.0", "A@1.0.0", "D@1.0.0")
        }, pattern: "E");

        Assert.Empty(result.Occurrences);
        Assert.Equal(["net8.0 E 3.0.0 A > B > E Computed"], Rows(result));
    }

    [Fact]
    public async Task TheCodeUsingAnUndeclaredPackageIsFlagged()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = Project("net8.0", "A@1.0.0", "D@1.0.0"),
            ["/App/Program.cs"] = "using Contoso.E; class Program { Thing thing; }"
        });

        var transitive = result.TransitiveDependencies!.ToDictionary(t => t.Package);
        Assert.Equal(PackageUsage.Used, transitive["E"].Usage);
        Assert.Equal(["Contoso.E.Thing"], transitive["E"].UsedTypes!.Select(t => t.Type));
        Assert.Equal(PackageSource.NuGetOrg, transitive["E"].Source);
        // F has no types of its own: those of its dependency E 3.0.0 count, as for a declared reference.
        Assert.Equal(PackageUsage.Used, transitive["F"].Usage);
        // The declared references keep their own usage: A has no types of its own, those of its dependencies count.
        Assert.Equal(PackageUsage.Used, result.Occurrences.Single(o => o.Package == "A").Usage);
    }

    [Fact]
    public async Task WithoutTransitiveResolutionThereAreNoTransitiveDependencies()
    {
        var api = new FakeRepositoryProvider().AddRepository("app", files: new Dictionary<string, string>
        {
            ["/App/App.csproj"] = Project("net8.0", "A@1.0.0")
        });

        var result = await RepositoryScanner.ScanRepositoryAsync(api, api.Repositories.Single(),
            DependencyMatcher.Create(["*"], []), null, CancellationToken.None);

        Assert.Empty(result.TransitiveDependencies ?? []);
    }

    [Fact]
    public async Task TransitiveDependenciesHaveTheirOwnReportAndLeaveDependenciesUnchanged()
    {
        var occurrence = new PackageOccurrence("project", "api", "main", "abc", "/App.csproj", "A", "1.0.0",
            PackageKind.Reference, VersionSource.Explicit, ["Glob:*"], "https://example/app");
        var transitive = new TransitiveDependency("project", "api", "main", "abc", "/App.csproj", "net8.0", "E", "1.0.0",
            "A > B > E", DependencyResolution.Computed, ["Glob:*"], PackageUsage.Used, PackageSource.NuGetOrg);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var without = new ScanReport(DateTimeOffset.UnixEpoch, "origin", [], [occurrence], []);
            await ReportWriter.SaveAsync(without, directory, CancellationToken.None);
            var dependencies = await File.ReadAllTextAsync(Path.Combine(directory, "dependencies.csv"));
            Assert.DoesNotContain("TransitiveDependencies", await File.ReadAllTextAsync(Path.Combine(directory, "report.json")));

            await ReportWriter.SaveAsync(without with { TransitiveDependencies = [transitive] }, directory,
                CancellationToken.None);
            Assert.Equal(dependencies, await File.ReadAllTextAsync(Path.Combine(directory, "dependencies.csv")));
            Assert.Equal(
            [
                "Project;Repository;Branch;Commit;File;Framework;Package;Version;Via;Resolution;MatchedFilters;Usage;Source;Feed",
                "project;api;main;abc;/App.csproj;net8.0;E;1.0.0;A > B > E;Computed;Glob:*;Used;NuGetOrg;"
            ], await File.ReadAllLinesAsync(Path.Combine(directory, "transitive-dependencies.csv")));
            Assert.Contains("\"Resolution\": \"Computed\"",
                await File.ReadAllTextAsync(Path.Combine(directory, "report.json")));

            // A later run without --transitive in the same folder leaves no stale file behind.
            await ReportWriter.SaveAsync(without, directory, CancellationToken.None);
            Assert.False(File.Exists(Path.Combine(directory, "transitive-dependencies.csv")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

internal static class TransitivePackages
{
    /// <summary>Adds a package version with its .nuspec and, when given, an assembly compiled from source.</summary>
    public static FakePackageFeed Package(this FakePackageFeed feed, string id, string version,
        string dependencies = "", string? types = null)
    {
        var nuspec = TestPackages.Nuspec(id, version, dependencies);
        var nupkg = types is null
            ? TestPackages.Nupkg(id, version, nuspec)
            : TestPackages.Nupkg(id, version, nuspec, ($"lib/net8.0/{id}.dll", types));
        return feed.Add(id, version, nuspec, nupkg);
    }
}
