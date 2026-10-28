using NuGet.Versioning;
using Xunit;

namespace DependencyInventory.Tests;

public sealed class PackageUsageTests
{
    private const string MessagingSource = """
        namespace Contoso.Messaging
        {
            public class Bus { }
            public interface IHandler<T> { }
            public sealed class SubscribeAttribute : System.Attribute { }
            internal class Hidden { }
        }
        namespace Microsoft.Extensions.DependencyInjection
        {
            public static class MessagingExtensions
            {
                public static object AddMessaging(this object services) => services;
            }
        }
        """;

    private static readonly byte[] Messaging = TestPackages.Nupkg("Contoso.Messaging", "1.0.0", null,
        ("lib/net8.0/Contoso.Messaging.dll", MessagingSource),
        ("lib/net8.0/fr/Contoso.Messaging.resources.dll", new byte[] { 1, 2, 3 }));

    private static readonly byte[] Other = TestPackages.Nupkg("Contoso.Other", "1.0.0", null,
        ("lib/netstandard2.0/Contoso.Other.dll", "namespace Contoso.Other { public class Bus { } }"));

    private static readonly byte[] Analyzers = TestPackages.Nupkg("Contoso.Analyzers", "1.0.0", null,
        ("analyzers/dotnet/cs/Contoso.Analyzers.dll", new byte[] { 0 }));

    // A metapackage: no assembly, its types come from its dependency.
    private static readonly byte[] Testing = TestPackages.Nupkg("Contoso.Testing", "1.0.0",
        TestPackages.Nuspec("Contoso.Testing", "1.0.0", """
            <group targetFramework="net8.0"><dependency id="Contoso.Testing.Core" version="[1.0.0]" /></group>
            <group targetFramework="net462"><dependency id="Contoso.Testing.Legacy" version="1.0.0" /></group>
            """),
        ("lib/net8.0/_._", ""));

    private static readonly byte[] TestingCore = TestPackages.Nupkg("Contoso.Testing.Core", "1.0.0", null,
        ("lib/net8.0/Contoso.Testing.Core.dll", """
            namespace Contoso.Testing
            {
                public sealed class FactAttribute : System.Attribute { }
                public static class Assert { public static void True(bool value) { } }
            }
            """));

    private static readonly byte[] Design = TestPackages.Nupkg("Contoso.Design", "1.0.0",
        TestPackages.Nuspec("Contoso.Design", "1.0.0", developmentDependency: true),
        ("lib/net8.0/Contoso.Design.dll", "namespace Contoso.Design { public class Tool { } }"));

    /// <summary>Two feeds then a nuget.org stand-in; Contoso.Design is a nuget.org package cached in the first feed.</summary>
    private static (FakePackageFeed FeedA, FakePackageFeed FeedB, FakePackageFeed NuGetOrg) Feeds() => (
        new FakePackageFeed()
            .AddPackage("Contoso.Messaging", "1.0.0", Messaging)
            .AddPackage("Contoso.Analyzers", "1.0.0", Analyzers)
            .Add("Contoso.Design", "1.0.0", null, Design).Cache("Contoso.Design", "1.0.0"),
        new FakePackageFeed()
            .AddPackage("Contoso.Other", "1.0.0", Other),
        new FakePackageFeed()
            .Add("Contoso.Other", "1.0.0", null, Other)
            .Add("Contoso.Testing", "1.0.0", null, Testing)
            .Add("Contoso.Testing.Core", "1.0.0", null, TestingCore));

    private static PackageCatalog Catalog(FakePackageFeed feedA, FakePackageFeed feedB, FakePackageFeed nugetOrg,
        string? cache = null, string? globalPackages = null) =>
        new([
            new PackageSourceEntry(feedA, PackageSource.Feed, "feed-a"),
            new PackageSourceEntry(feedB, PackageSource.Feed, "project/feed-b"),
            new PackageSourceEntry(nugetOrg, PackageSource.NuGetOrg, null)
        ], cache, globalPackages);

    private static string Project(string sdk, params string[] references) =>
        $"""
        <Project Sdk="{sdk}">
          <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
          <ItemGroup>
            {string.Concat(references.Select(r => r.StartsWith('<') ? r :
                $"""<PackageReference Include="{r.Split('@')[0]}" Version="{r.Split('@')[1]}" />"""))}
          </ItemGroup>
        </Project>
        """;

    private static FakeRepositoryProvider Repository() => new FakeRepositoryProvider().AddRepository("app",
        files: new Dictionary<string, string>
        {
            ["/Directory.Build.props"] = """
                <Project>
                  <PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
                  <ItemGroup><PackageReference Include="Contoso.Analyzers" Version="1.0.0" /></ItemGroup>
                </Project>
                """,
            ["/src/Api/Api.csproj"] = Project("Microsoft.NET.Sdk.Web", "Contoso.Messaging@1.0.0",
                "Contoso.Other@1.0.0", "Contoso.Design@1.0.0", "Contoso.Missing@1.0.0", "Contoso.Messaging@$(V)"),
            ["/src/Api/Program.cs"] = """
                using Contoso.Messaging;
                var bus = new Bus();
                builder.Services.AddMessaging();
                """,
            ["/src/Api/Orders/OrderHandler.cs"] = """
                namespace Contoso.Messaging.Orders;
                [Subscribe]
                public sealed class OrderHandler : IHandler<Order> { }
                """,
            ["/src/Api/obj/Generated.cs"] = "using Contoso.Other; class G { Bus b; }",
            ["/src/Worker/Worker.csproj"] = Project("Microsoft.NET.Sdk", "Contoso.Messaging@1.0.0"),
            ["/src/Worker/Worker.cs"] = "class Worker { global::Contoso.Messaging.Bus bus; }",
            ["/src/Tests/Tests.csproj"] = Project("Microsoft.NET.Sdk", """<Using Include="Contoso.Testing" />""",
                "Contoso.Testing@1.0.0"),
            ["/src/Tests/BusTests.cs"] = """
                public class BusTests
                {
                    [Fact] public void Works() => Assert.True(true);
                }
                """,
            ["/src/Legacy/Legacy.vbproj"] = Project("Microsoft.NET.Sdk", "Contoso.Messaging@1.0.0"),
            ["/src/Legacy/Module.vb"] = "Module M\nEnd Module"
        });

    private static async Task<(IReadOnlyList<PackageOccurrence> References, RepositoryDiagnostic Diagnostic)> ScanAsync(
        FakeRepositoryProvider api, PackageCatalog catalog, bool usedTypes = true)
    {
        var diagnostic = new RepositoryDiagnostic { Project = "project", Repository = "app" };
        var result = await RepositoryScanner.ScanRepositoryAsync(api, api.Repositories.Single(),
            DependencyMatcher.Create(["Contoso.*"], []), diagnostic, CancellationToken.None,
            usage: new PackageUsageAnalyzer(catalog, usedTypes));
        return (result.Occurrences.Where(o => o.Kind == PackageKind.Reference).ToArray(), diagnostic);
    }

    private static PackageOccurrence Find(IEnumerable<PackageOccurrence> references, string file, string package,
        string? version = "1.0.0") =>
        references.Single(o => o.File == file && o.Package == package && o.Version == version);

    // --- Usage

    [Theory]
    [InlineData("/src/Api/Api.csproj", "Contoso.Messaging", PackageUsage.Used)]
    [InlineData("/src/Api/Api.csproj", "Contoso.Other", PackageUsage.Unused)]
    [InlineData("/src/Api/Api.csproj", "Contoso.Design", PackageUsage.BuildOnly)]
    [InlineData("/src/Api/Api.csproj", "Contoso.Missing", PackageUsage.Undetermined)]
    [InlineData("/src/Worker/Worker.csproj", "Contoso.Messaging", PackageUsage.Used)]
    [InlineData("/src/Tests/Tests.csproj", "Contoso.Testing", PackageUsage.Used)]
    [InlineData("/src/Legacy/Legacy.vbproj", "Contoso.Messaging", PackageUsage.Undetermined)]
    [InlineData("/Directory.Build.props", "Contoso.Analyzers", PackageUsage.BuildOnly)]
    public async Task EveryReferenceGetsTheUsageOfItsPackage(string file, string package, PackageUsage expected)
    {
        var (feedA, feedB, nugetOrg) = Feeds();
        var (references, _) = await ScanAsync(Repository(), Catalog(feedA, feedB, nugetOrg));

        Assert.Equal(expected, Find(references, file, package).Usage);
    }

    [Fact]
    public async Task UsedTypesComeFromUsingsEnclosingNamespacesAttributesAndExtensionMethods()
    {
        var (feedA, feedB, nugetOrg) = Feeds();
        var (references, _) = await ScanAsync(Repository(), Catalog(feedA, feedB, nugetOrg));

        // Bus and IHandler through using and the enclosing namespace, Subscribe as SubscribeAttribute, AddMessaging
        // through the implicit usings of the Web SDK; the internal type and the generated file are left out.
        Assert.Equal(
            [
                "Contoso.Messaging.Bus 1", "Contoso.Messaging.IHandler 1", "Contoso.Messaging.SubscribeAttribute 1",
                "Microsoft.Extensions.DependencyInjection.MessagingExtensions 1"
            ],
            Find(references, "/src/Api/Api.csproj", "Contoso.Messaging").UsedTypes!.Select(t => $"{t.Type} {t.Files}"));
        Assert.Equal(["Contoso.Messaging.Bus"],
            Find(references, "/src/Worker/Worker.csproj", "Contoso.Messaging").UsedTypes!.Select(t => t.Type));
        // The Using item of the project file imports the namespace of the metapackage's dependency.
        Assert.Equal(["Contoso.Testing.Assert", "Contoso.Testing.FactAttribute"],
            Find(references, "/src/Tests/Tests.csproj", "Contoso.Testing").UsedTypes!.Select(t => t.Type));
    }

    [Fact]
    public async Task UsedTypesAreOnlyListedOnRequest()
    {
        var (feedA, feedB, nugetOrg) = Feeds();
        var (references, _) = await ScanAsync(Repository(), Catalog(feedA, feedB, nugetOrg), usedTypes: false);

        Assert.All(references, o => Assert.Null(o.UsedTypes));
        Assert.Equal(PackageUsage.Used, Find(references, "/src/Api/Api.csproj", "Contoso.Messaging").Usage);
    }

    [Fact]
    public async Task TheReasonOfAnUndeterminedUsageIsInTheDiagnostics()
    {
        var (feedA, feedB, nugetOrg) = Feeds();
        var (references, diagnostic) = await ScanAsync(Repository(), Catalog(feedA, feedB, nugetOrg));

        Assert.Equal(PackageUsage.Undetermined, Find(references, "/src/Api/Api.csproj", "Contoso.Messaging", "$(V)").Usage);
        var reasons = diagnostic.Usages.Where(u => u.Usage == PackageUsage.Undetermined)
            .ToDictionary(u => $"{u.File} {u.Package} {u.Version}", u => u.Reason);
        Assert.Equal("Not found in feed feed-a, feed project/feed-b, nuget.org",
            reasons["/src/Api/Api.csproj Contoso.Missing 1.0.0"]);
        Assert.Contains("$(V)", reasons["/src/Api/Api.csproj Contoso.Messaging $(V)"]);
        Assert.Equal("Not analyzed (C# only): /src/Legacy/Legacy.vbproj.",
            reasons["/src/Legacy/Legacy.vbproj Contoso.Messaging 1.0.0"]);
    }

    [Fact]
    public async Task AReferenceOfADirectoryBuildPropsCoversTheProjectsUnderItsFolder()
    {
        var (feedA, feedB, nugetOrg) = Feeds();
        var (_, diagnostic) = await ScanAsync(Repository(), Catalog(feedA, feedB, nugetOrg));

        Assert.Equal(
            ["/src/Api/Api.csproj", "/src/Legacy/Legacy.vbproj", "/src/Tests/Tests.csproj", "/src/Worker/Worker.csproj"],
            diagnostic.Usages.Single(u => u.Package == "Contoso.Analyzers").Projects);
    }

    [Fact]
    public async Task AnUnreadableSourceFileMakesAnUnusedPackageUndetermined()
    {
        var (feedA, feedB, nugetOrg) = Feeds();
        var api = Repository().FailFile("app", "/src/Api/Program.cs");
        var (references, _) = await ScanAsync(api, Catalog(feedA, feedB, nugetOrg));

        Assert.Equal(PackageUsage.Undetermined, Find(references, "/src/Api/Api.csproj", "Contoso.Other").Usage);
        // Still used through the files that could be read.
        Assert.Equal(PackageUsage.Used, Find(references, "/src/Api/Api.csproj", "Contoso.Messaging").Usage);
    }

    [Fact]
    public async Task AProjectWithoutDirectoryBuildPropsIsAnalyzed()
    {
        var (feedA, feedB, nugetOrg) = Feeds();
        var api = new FakeRepositoryProvider().AddRepository("app", files: new Dictionary<string, string>
        {
            ["/Worker.csproj"] = Project("Microsoft.NET.Sdk", "Contoso.Messaging@1.0.0"),
            ["/Worker.cs"] = "using Contoso.Messaging; class Worker { Bus bus; }"
        });

        var (references, _) = await ScanAsync(api, Catalog(feedA, feedB, nugetOrg));

        Assert.Equal(PackageUsage.Used, Assert.Single(references).Usage);
    }

    [Fact]
    public async Task WithoutUsageAnalysisReferencesHaveNoUsage()
    {
        var api = Repository();
        var result = await RepositoryScanner.ScanRepositoryAsync(api, api.Repositories.Single(),
            DependencyMatcher.Create(["Contoso.*"], []), null, CancellationToken.None);

        Assert.All(result.Occurrences, o => Assert.Null(o.Usage));
    }

    // --- Package sources

    [Theory]
    [InlineData("/src/Api/Api.csproj", "Contoso.Messaging", PackageSource.Feed, "feed-a")]
    [InlineData("/src/Api/Api.csproj", "Contoso.Other", PackageSource.Feed, "project/feed-b")]
    [InlineData("/src/Api/Api.csproj", "Contoso.Design", PackageSource.NuGetOrg, null)]
    [InlineData("/src/Tests/Tests.csproj", "Contoso.Testing", PackageSource.NuGetOrg, null)]
    [InlineData("/src/Api/Api.csproj", "Contoso.Missing", null, null)]
    public async Task TheSourceIsTheRealOriginOfThePackage(string file, string package, PackageSource? source,
        string? feed)
    {
        var (feedA, feedB, nugetOrg) = Feeds();
        var (references, _) = await ScanAsync(Repository(), Catalog(feedA, feedB, nugetOrg));

        var occurrence = Find(references, file, package);
        Assert.Equal((source, feed), (occurrence.Source, occurrence.Feed));
    }

    [Fact]
    public async Task AFailingSourceIsReportedAndTheNextOneIsRead()
    {
        var (feedA, feedB, nugetOrg) = Feeds();
        feedB.FailPublications("Contoso.Other");
        var catalog = Catalog(feedA, feedB, nugetOrg);

        var resolution = await catalog.ResolveAsync("Contoso.Other", NuGetVersion.Parse("1.0.0"), default);

        Assert.Equal(PackageSource.NuGetOrg, resolution.Source);
        Assert.Equal(["feed project/feed-b: feed API unavailable"], catalog.SourceFailures);
    }

    [Fact]
    public async Task EachPackageVersionIsReadOncePerRun()
    {
        var (feedA, feedB, nugetOrg) = Feeds();
        var catalog = Catalog(feedA, feedB, nugetOrg);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            catalog.ResolveAsync("contoso.messaging", NuGetVersion.Parse("1.0"), default)));

        Assert.Equal(1, feedA.PackageReads);
    }

    [Fact]
    public async Task TheContentOfAVersionIsCachedForTheNextRuns()
    {
        var cache = Directory.CreateTempSubdirectory("depinv-cache-").FullName;
        try
        {
            var (feedA, feedB, nugetOrg) = Feeds();
            var first = await Catalog(feedA, feedB, nugetOrg, cache)
                .ResolveAsync("Contoso.Messaging", NuGetVersion.Parse("1.0.0"), default);
            var second = await Catalog(feedA, feedB, nugetOrg, cache)
                .ResolveAsync("Contoso.Messaging", NuGetVersion.Parse("1.0.0"), default);

            Assert.Equal(1, feedA.PackageReads);
            Assert.Equal("Cache", second.ReadFrom);
            Assert.Equal(first.Content!.Types, second.Content!.Types);
            Assert.Equal(first.Content.ExtensionMethods, second.Content.ExtensionMethods);
            Assert.Equal((PackageSource.Feed, "feed-a"), (second.Source, second.Feed));
        }
        finally { Directory.Delete(cache, true); }
    }

    [Fact]
    public async Task TheGlobalPackagesFolderIsReadBeforeAnyDownload()
    {
        var packages = Directory.CreateTempSubdirectory("depinv-packages-").FullName;
        try
        {
            var folder = Path.Combine(packages, "contoso.messaging", "1.0.0");
            System.IO.Compression.ZipFile.ExtractToDirectory(new MemoryStream(Messaging), folder);
            File.WriteAllText(Path.Combine(folder, ".nupkg.metadata"), "{}");
            var (feedA, feedB, nugetOrg) = Feeds();

            var resolution = await Catalog(feedA, feedB, nugetOrg, globalPackages: packages)
                .ResolveAsync("Contoso.Messaging", NuGetVersion.Parse("1.0.0"), default);

            Assert.Equal(0, feedA.PackageReads);
            Assert.Equal("GlobalPackagesFolder", resolution.ReadFrom);
            Assert.Contains(new PackageType("Contoso.Messaging", "Bus"), resolution.Content!.Types);
        }
        finally { Directory.Delete(packages, true); }
    }

    // --- Package content

    [Fact]
    public void ThePackageExposesItsPublicTopLevelTypesAndExtensionMethods()
    {
        var content = PackageContentReader.ReadPackage(new MemoryStream(Messaging));

        Assert.Equal(["Contoso.Messaging.Bus", "Contoso.Messaging.IHandler", "Contoso.Messaging.SubscribeAttribute",
                "Microsoft.Extensions.DependencyInjection.MessagingExtensions"],
            content.Types.Select(t => t.FullName));
        Assert.Equal([new ExtensionMethod("AddMessaging", "Microsoft.Extensions.DependencyInjection",
            "Microsoft.Extensions.DependencyInjection.MessagingExtensions")], content.ExtensionMethods);
        Assert.False(content.HasBuildAssets);
    }

    [Fact]
    public void TheReferenceAssembliesOfAFrameworkHideItsImplementation()
    {
        var package = TestPackages.Nupkg("A", "1.0.0", null,
            ("ref/net8.0/A.dll", "namespace A { public class Contract { } }"),
            ("lib/net8.0/A.dll", "namespace A { public class Contract { } public class Implementation { } }"),
            ("lib/net462/A.dll", "namespace A { public class Legacy { } }"));

        var content = PackageContentReader.ReadPackage(new MemoryStream(package));

        Assert.Equal(["A.Contract", "A.Legacy"], content.Types.Select(t => t.FullName));
    }

    [Fact]
    public void BuildAssetsAndDevelopmentDependenciesAreRecognized()
    {
        var analyzers = PackageContentReader.ReadPackage(new MemoryStream(Analyzers));
        var design = PackageContentReader.ReadPackage(new MemoryStream(Design));
        var testing = PackageContentReader.ReadPackage(new MemoryStream(Testing));

        Assert.True(analyzers is { HasBuildAssets: true, HasTypes: false });
        Assert.True(design is { DevelopmentDependency: true, HasTypes: true });
        Assert.Equal(["Contoso.Testing.Core", "Contoso.Testing.Legacy"], testing.Dependencies.Select(d => d.Id));
    }

    // --- Versions

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("[1.0,2.0)", "1.0.0")]
    [InlineData("[1.5]", "1.5.0")]
    [InlineData("(1.0,)", null)]
    [InlineData("1.*", null)]
    [InlineData("$(SerilogVersion)", null)]
    [InlineData(null, null)]
    public void TheVersionReadIsTheLiteralOneOrTheMinimumOfARange(string? declared, string? expected)
    {
        Assert.Equal(expected, PackageUsageAnalyzer.ResolveVersion(declared)?.ToNormalizedString());
    }

    // --- Source code

    [Fact]
    public void TheParserSeparatesImportsFromReferences()
    {
        var symbols = SourceCodeParser.Parse("/A.cs", """
            using System.Text;
            global using Contoso.Global;
            global using static Contoso.Helpers;
            using Alias = Contoso.Messaging.Bus;
            namespace Contoso.App.Orders
            {
                [Contoso.Testing.Fact]
                class Handler
                {
                    void Run(Order order) => order?.Publish().Sync();
                }
            }
            """);

        Assert.Equal(["Contoso", "Contoso.App", "Contoso.App.Orders", "System.Text"], symbols.Imports.Order());
        Assert.Equal(["Contoso.Global"], symbols.GlobalImports);
        Assert.Equal(["Contoso.Helpers"], symbols.GlobalQualifiedNames);
        Assert.Contains("Contoso.Messaging.Bus", symbols.QualifiedNames);
        Assert.Contains("Contoso.Testing.FactAttribute", symbols.QualifiedNames);
        Assert.Contains("FactAttribute", symbols.Names);
        Assert.Contains("Order", symbols.Names);
        Assert.DoesNotContain("App", symbols.Names);
        Assert.Equal(["Publish", "Sync"], symbols.InvokedMembers.Order());
    }

    [Theory]
    [InlineData("Microsoft.NET.Sdk", "enable", "System.Linq", true)]
    [InlineData("Microsoft.NET.Sdk", "enable", "Microsoft.Extensions.Hosting", false)]
    [InlineData("Microsoft.NET.Sdk.Worker", "true", "Microsoft.Extensions.Hosting", true)]
    [InlineData("Microsoft.NET.Sdk.Web", "enable", "Microsoft.AspNetCore.Builder", true)]
    [InlineData("Microsoft.NET.Sdk.Web", "disable", "System.Linq", false)]
    public void ImplicitUsingsDependOnTheSdk(string sdk, string implicitUsings, string ns, bool imported)
    {
        var imports = ProjectImports.Parse(
            $"""<Project Sdk="{sdk}"><PropertyGroup><ImplicitUsings>{implicitUsings}</ImplicitUsings></PropertyGroup></Project>""");

        Assert.Equal(imported, imports.Namespaces.Contains(ns));
    }

    [Fact]
    public void UsingItemsImportANamespaceOrNameAType()
    {
        var imports = ProjectImports.Parse("""
            <Project><ItemGroup>
              <Using Include="Contoso.Testing" />
              <Using Include="Contoso.Testing.Assert" Static="true" />
              <Using Include="Contoso.Messaging.Bus" Alias="MessageBus" />
              <Using Remove="System.Net.Http" />
            </ItemGroup></Project>
            """);

        Assert.Equal(["Contoso.Testing"], imports.Usings);
        Assert.Equal(["Contoso.Testing.Assert", "Contoso.Messaging.Bus"], imports.QualifiedUsings);
    }
}
