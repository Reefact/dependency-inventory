using Xunit;

namespace DependencyInventory.Tests;

public sealed class UsedTransitivelyTests
{
    /// <summary>
    /// Messaging and Events have types of their own and bring Telemetry, which has types too; Tools has types and brings
    /// nothing; Bundle has types and brings Messaging.
    /// </summary>
    private static PackageCatalog Catalog() => new([new PackageSourceEntry(new FakePackageFeed { Origin = "https://api.nuget.org/v3/index.json" }
        .Package("Messaging", "1.0.0", """<dependency id="Telemetry" version="1.0.0" />""",
            types: "namespace Contoso.Messaging { public class Bus { } }")
        .Package("Telemetry", "1.0.0", types: "namespace Contoso.Telemetry { public class Tracer { } }")
        .Package("Events", "1.0.0", """<dependency id="Telemetry" version="1.0.0" />""",
            types: "namespace Contoso.Events { public class Event { } }")
        .Package("Bundle", "1.0.0", """<dependency id="Messaging" version="1.0.0" />""",
            types: "namespace Contoso.Bundle { public class Kit { } }")
        .Package("Tools", "1.0.0", types: "namespace Contoso.Tools { public class Hammer { } }"),
        PackageSource.NuGetOrg, null)]);

    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Messaging" Version="1.0.0" />
            <PackageReference Include="Tools" Version="1.0.0" />
          </ItemGroup>
        </Project>
        """;

    private const string UsesTelemetry = "using Contoso.Telemetry; class Program { Tracer tracer; }";

    private static async Task<(RepositoryScanResult Result, RepositoryDiagnostic Diagnostic)> ScanAsync(
        Dictionary<string, string> files, string pattern = "*", bool transitive = true)
    {
        var api = new FakeRepositoryProvider().AddRepository("app", files: files);
        var diagnostic = new RepositoryDiagnostic { Scope = "project", Repository = "app" };
        var catalog = Catalog();
        var result = await RepositoryScanner.ScanRepositoryAsync(api, api.Repositories.Single(),
            DependencyMatcher.Create([pattern], []), diagnostic, CancellationToken.None,
            usage: new PackageUsageAnalyzer(catalog, true), transitive: transitive ? new TransitiveResolver(catalog) : null);
        return (result, diagnostic);
    }

    private static PackageUsage? Usage(RepositoryScanResult result, string package) =>
        result.Occurrences.Single(o => o.Package == package).Usage;

    [Fact]
    public async Task AReferenceThatBringsAPackageTheCodeUsesIsUsedTransitively()
    {
        var (result, diagnostic) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = Project,
            ["/App/Program.cs"] = UsesTelemetry
        });

        Assert.Equal(PackageUsage.UsedTransitively, Usage(result, "Messaging"));
        Assert.Equal(PackageUsage.Unused, Usage(result, "Tools"));
        Assert.Equal(PackageUsage.Used, result.TransitiveDependencies!.Single(t => t.Package == "Telemetry").Usage);
        var usage = diagnostic.Usages.Single(u => u.Package == "Messaging");
        Assert.Equal(PackageUsage.UsedTransitively, usage.Usage);
        Assert.Equal("No type of its own is used, but it is the only reference bringing Telemetry, which the code uses.", usage.Reason);
    }

    [Fact]
    public async Task AReferenceWhoseOwnTypesAreUsedStaysUsed()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = Project,
            ["/App/Program.cs"] = "using Contoso.Messaging; using Contoso.Telemetry; class Program { Bus bus; Tracer tracer; }"
        });

        Assert.Equal(PackageUsage.Used, Usage(result, "Messaging"));
    }

    [Fact]
    public async Task WithoutTransitiveResolutionTheReferenceStaysUnused()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = Project,
            ["/App/Program.cs"] = UsesTelemetry
        }, transitive: false);

        Assert.Equal(PackageUsage.Unused, Usage(result, "Messaging"));
    }

    [Fact]
    public async Task OnlyTheTransitivePackagesMatchingTheFiltersCount()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = Project,
            ["/App/Program.cs"] = UsesTelemetry
        }, pattern: "Messaging");

        Assert.Equal(PackageUsage.Unused, Usage(result, "Messaging"));
    }

    [Fact]
    public async Task AReferenceInASharedFileIsUsedTransitivelyWhenOneOfItsProjectsUsesWhatItBrings()
    {
        var (result, diagnostic) = await ScanAsync(new()
        {
            ["/Directory.Build.props"] = """
                <Project>
                  <ItemGroup><PackageReference Include="Messaging" Version="1.0.0" /></ItemGroup>
                </Project>
                """,
            ["/App/App.csproj"] = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""",
            ["/App/Program.cs"] = UsesTelemetry,
            ["/Lib/Lib.csproj"] = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""",
            ["/Lib/Class.cs"] = "class Class { }"
        });

        var messaging = result.Occurrences.Single(o => o.Package == "Messaging");
        Assert.Equal("/Directory.Build.props", messaging.File);
        Assert.Equal(PackageUsage.UsedTransitively, messaging.Usage);
        Assert.Equal(PackageUsage.UsedTransitively, diagnostic.Usages.Single(u => u.Package == "Messaging").Usage);
    }

    [Fact]
    public async Task AReferenceIsNotCreditedWithWhatAnotherProjectUses()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                  <ItemGroup><PackageReference Include="Messaging" Version="1.0.0" /></ItemGroup>
                </Project>
                """,
            ["/App/Program.cs"] = "class Program { }",
            ["/Other/Other.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                  <ItemGroup><PackageReference Include="Messaging" Version="1.0.0" /></ItemGroup>
                </Project>
                """,
            ["/Other/Program.cs"] = UsesTelemetry
        });

        Assert.Equal(PackageUsage.Unused, result.Occurrences.Single(o => o.File == "/App/App.csproj").Usage);
        Assert.Equal(PackageUsage.UsedTransitively, result.Occurrences.Single(o => o.File == "/Other/Other.csproj").Usage);
    }

    private static string ProjectWith(params string[] packages) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
          <ItemGroup>{string.Concat(packages.Select(p => $"""<PackageReference Include="{p}" Version="1.0.0" />"""))}</ItemGroup>
        </Project>
        """;

    [Fact]
    public async Task TwoReferencesBringingTheSamePackageAreNeitherIndispensable()
    {
        var (result, diagnostic) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = ProjectWith("Messaging", "Events"),
            ["/App/Program.cs"] = UsesTelemetry
        });

        // Removing either one keeps Telemetry through the other: neither is reported as needed.
        Assert.Equal(PackageUsage.Unused, Usage(result, "Messaging"));
        Assert.Equal(PackageUsage.Unused, Usage(result, "Events"));
        Assert.Equal(["Messaging", "Events"], result.TransitiveDependencies!.Single(t => t.Package == "Telemetry").Roots);
        Assert.Equal("No type of its own is used; it brings Telemetry, which the code uses, but so do Events.",
            diagnostic.Usages.Single(u => u.Package == "Messaging").Reason);
    }

    [Fact]
    public async Task AReferenceWhosePackageIsAlsoBroughtByAUsedReferenceStaysUnused()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = ProjectWith("Messaging", "Events"),
            ["/App/Program.cs"] = "using Contoso.Events; using Contoso.Telemetry; class Program { Event e; Tracer tracer; }"
        });

        Assert.Equal(PackageUsage.Used, Usage(result, "Events"));
        Assert.Equal(PackageUsage.Unused, Usage(result, "Messaging"));
    }

    [Fact]
    public async Task ALockFileGivesEveryReferenceBringingThePackage()
    {
        var (result, _) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = ProjectWith("Messaging", "Events"),
            ["/App/Program.cs"] = UsesTelemetry,
            ["/App/packages.lock.json"] = """
                {
                  "version": 1,
                  "dependencies": {
                    "net8.0": {
                      "Messaging": { "type": "Direct", "requested": "[1.0.0, )", "resolved": "1.0.0", "dependencies": { "Telemetry": "1.0.0" } },
                      "Events": { "type": "Direct", "requested": "[1.0.0, )", "resolved": "1.0.0" },
                      "Telemetry": { "type": "Transitive", "resolved": "1.0.0" }
                    }
                  }
                }
                """
        });

        var telemetry = result.TransitiveDependencies!.Single(t => t.Package == "Telemetry");
        Assert.Equal(DependencyResolution.LockFile, telemetry.Resolution);
        Assert.Equal(["Messaging"], telemetry.Roots);
        Assert.Equal(PackageUsage.UsedTransitively, Usage(result, "Messaging"));
        Assert.Equal(PackageUsage.Unused, Usage(result, "Events"));
    }

    [Fact]
    public async Task ThePathsThroughAnotherDeclaredReferenceCount()
    {
        // Messaging is declared, but Bundle brings it too, and Telemetry with it: removing Messaging keeps Telemetry.
        var (result, _) = await ScanAsync(new()
        {
            ["/App/App.csproj"] = ProjectWith("Messaging", "Bundle"),
            ["/App/Program.cs"] = UsesTelemetry
        });

        Assert.Equal(["Messaging", "Bundle"], result.TransitiveDependencies!.Single(t => t.Package == "Telemetry").Roots);
        Assert.Equal(PackageUsage.Unused, Usage(result, "Messaging"));
        Assert.Equal(PackageUsage.Unused, Usage(result, "Bundle"));
    }
}
