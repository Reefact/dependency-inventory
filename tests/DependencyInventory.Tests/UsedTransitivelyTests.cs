using Xunit;

namespace DependencyInventory.Tests;

public sealed class UsedTransitivelyTests
{
    /// <summary>
    /// Messaging has types of its own and brings Telemetry, which has types too; Tools has types and brings nothing.
    /// </summary>
    private static PackageCatalog Catalog() => new([new PackageSourceEntry(new FakePackageFeed { Origin = "https://api.nuget.org/v3/index.json" }
        .Package("Messaging", "1.0.0", """<dependency id="Telemetry" version="1.0.0" />""",
            types: "namespace Contoso.Messaging { public class Bus { } }")
        .Package("Telemetry", "1.0.0", types: "namespace Contoso.Telemetry { public class Tracer { } }")
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
        var diagnostic = new RepositoryDiagnostic { Project = "project", Repository = "app" };
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
        Assert.Equal("No type of its own is used, but it brings Telemetry, which the code uses.", usage.Reason);
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
}
