using System.IO.Compression;
using System.Net;
using System.Text;
using Xunit;

namespace DependencyInventory.Tests;

public sealed class FeedTests
{
    private static string Nuspec(string body) =>
        $"""<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>X</id><version>1.0.0</version>{body}</metadata></package>""";

    private static byte[] Nupkg(params string[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries) archive.CreateEntry(entry);
        return buffer.ToArray();
    }

    // --- --package

    [Theory]
    [InlineData("Stime.Core.Vnext.Messaging@2.0.4", "Stime.Core.Vnext.Messaging", "2.0.4")]
    [InlineData(" Stime.Core.Vnext.Messaging @ 2.1.0-beta.6 ", "Stime.Core.Vnext.Messaging", "2.1.0-beta.6")]
    [InlineData("Stime.Core.Vnext.Messaging", "Stime.Core.Vnext.Messaging", null)]
    public void PackageRequestReadsAnIdAndAnOptionalVersion(string value, string id, string? version)
    {
        Assert.Equal(new PackageRequest(id, version), PackageRequest.Parse(value));
    }

    [Theory]
    [InlineData("@1.0.0")]
    [InlineData("Stime.Core@")]
    [InlineData("Stime.Core@latest")]
    [InlineData("Stime Core@1.0.0")]
    public void PackageRequestRejectsAnInvalidValue(string value)
    {
        Assert.Throws<ArgumentException>(() => PackageRequest.Parse(value));
    }

    // --- .nuspec and .nupkg

    [Fact]
    public void NuspecGivesTheFrameworksAndDependenciesOfEveryGroup()
    {
        var content = NuspecParser.Parse(Nuspec("""
            <dependencies>
              <group targetFramework="net8.0">
                <dependency id="Stime.Core.Vnext.Logging.Serilog" version="2.0.4" exclude="Build,Analyzers" />
              </group>
              <group targetFramework=".NETStandard2.0">
                <dependency id="Microsoft.AspNetCore.Http.Abstractions" version="[2.2.0, )" />
              </group>
            </dependencies>
            <frameworkReferences>
              <group targetFramework="net8.0"><frameworkReference name="Microsoft.AspNetCore.App" /></group>
            </frameworkReferences>
            """));

        Assert.Equal(["net8.0", "netstandard2.0"], content.Frameworks);
        Assert.Equal(
        [
            new NuspecDependency("net8.0", "Stime.Core.Vnext.Logging.Serilog", "2.0.4", DependencyKind.Package),
            new NuspecDependency("netstandard2.0", "Microsoft.AspNetCore.Http.Abstractions", "[2.2.0, )", DependencyKind.Package),
            new NuspecDependency("net8.0", "Microsoft.AspNetCore.App", null, DependencyKind.FrameworkReference)
        ], content.Dependencies);
    }

    [Fact]
    public void NuspecGivesEveryMetadata()
    {
        var content = NuspecParser.Parse("""
            <package><metadata minClientVersion="5.3">
              <id>Stime.Core.Vnext.Messaging</id><version>2.0.4</version><title>Messaging</title>
              <authors>Stime</authors><owners>Platform</owners><description>Messaging building blocks.</description>
              <summary>Bus</summary><releaseNotes>Fixes</releaseNotes><copyright>Stime 2024</copyright>
              <language>en-US</language><tags>vnext messaging</tags><projectUrl>https://example/project</projectUrl>
              <iconUrl>https://example/icon.png</iconUrl><icon>icon.png</icon><readme>README.md</readme>
              <license type="expression">MIT</license><licenseUrl>https://aka.ms/deprecateLicenseUrl</licenseUrl>
              <requireLicenseAcceptance>false</requireLicenseAcceptance><developmentDependency>true</developmentDependency>
              <serviceable>true</serviceable>
              <packageTypes><packageType name="Dependency" /><packageType name="DotnetTool" /></packageTypes>
              <repository type="git" url="https://dev.azure.com/acme/_git/messaging" branch="refs/heads/master" commit="abc123" />
            </metadata></package>
            """);

        Assert.Equal(new PackageMetadata("Stime.Core.Vnext.Messaging", "Messaging", "Stime", "Platform",
            "Messaging building blocks.", "Bus", "Fixes", "Stime 2024", "en-US", "vnext messaging",
            "https://example/project", "https://example/icon.png", "icon.png", "README.md", "MIT", "expression",
            "https://aka.ms/deprecateLicenseUrl", "false", "true", "true", "5.3", "Dependency | DotnetTool", "git",
            "https://dev.azure.com/acme/_git/messaging", "refs/heads/master", "abc123"), content.Metadata);
    }

    [Theory]
    [InlineData(".NETFramework4.7.2", "net472")]
    [InlineData(".NETCoreApp3.1", "netcoreapp3.1")]
    [InlineData("net8.0-windows7.0", "net8.0-windows7.0")]
    [InlineData("NET10.0", "net10.0")]
    public void NuspecFrameworksAreShortMonikers(string declared, string expected)
    {
        var content = NuspecParser.Parse(Nuspec($"""<dependencies><group targetFramework="{declared}" /></dependencies>"""));

        Assert.Equal([expected], content.Frameworks);
    }

    [Fact]
    public void UngroupedDependenciesApplyToEveryFramework()
    {
        var content = NuspecParser.Parse(Nuspec("""<dependencies><dependency id="Newtonsoft.Json" version="9.0.1" /></dependencies>"""));

        Assert.Empty(content.Frameworks);
        Assert.Equal([new NuspecDependency(null, "Newtonsoft.Json", "9.0.1", DependencyKind.Package)], content.Dependencies);
    }

    [Fact]
    public void PackageFoldersGiveTheFrameworksOfLibAndRef()
    {
        using var package = new MemoryStream(Nupkg("lib/net8.0/A.dll", "lib/net8.0/A.xml", "ref/netstandard2.0/A.dll",
            "lib/net462/A.dll", "lib/A.dll", "content/net8.0/readme.txt", "A.nuspec"));

        Assert.Equal(["net8.0", "netstandard2.0", "net462"], NuspecParser.FrameworksFromPackage(package));
    }

    // --- --feed

    [Theory]
    [InlineData("ecommercev3-nuget",
        "https://pkgs.dev.azure.com/StimeEcommerceITM/_packaging/ecommercev3-nuget/nuget/v3/index.json",
        "https://feeds.dev.azure.com/StimeEcommerceITM/_apis/packaging/Feeds/ecommercev3-nuget/packages")]
    [InlineData("ECommerce/applications",
        "https://pkgs.dev.azure.com/StimeEcommerceITM/ECommerce/_packaging/applications/nuget/v3/index.json",
        "https://feeds.dev.azure.com/StimeEcommerceITM/ECommerce/_apis/packaging/Feeds/applications/packages")]
    public void FeedNameGivesTheOrganizationOrProjectFeedUrls(string name, string index, string packages)
    {
        var feed = AzureFeed.Parse(AzureOrigin.Parse("https://dev.azure.com/StimeEcommerceITM/ECommerce"), name);

        Assert.Equal(index, feed.IndexUrl);
        Assert.Equal(packages, feed.PackagesApiUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a/b/c")]
    [InlineData("/feed")]
    [InlineData("../feed")]
    public void FeedNameMustBeAFeedOrAProjectAndAFeed(string? name)
    {
        Assert.Throws<ArgumentException>(() =>
            AzureFeed.Parse(AzureOrigin.Parse("https://dev.azure.com/acme/project"), name));
    }

    // --- --versions-from

    [Fact]
    public void CsvValuesMayBeQuoted()
    {
        var rows = VersionsFile.ParseCsv("﻿Package;Version;Url\r\n\"A;B\";1.0.0;\"say \"\"hi\"\"\r\nthere\"\n\nC;2.0.0;x");

        Assert.Equal(
        [
            ["Package", "Version", "Url"],
            ["A;B", "1.0.0", "say \"hi\"\r\nthere"],
            ["C", "2.0.0", "x"]
        ], rows);
    }

    [Fact]
    public async Task VersionsFromADependenciesCsvSkipThoseWithoutALiteralVersion()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path,
                "Project;Repository;Package;Version;Kind\n" +
                "p;r;Stime.Core.Vnext.Messaging;2.0.4;Reference\n" +
                "p;r;Stime.Core.Vnext.Caching;1.0.0-beta.25;Reference\n" +
                "p;r;Stime.Core.Vnext.Hosting;;Reference\n" +
                "p;r;Stime.Core.Vnext.Hosting;$(HostingVersion);Reference\n" +
                "p;r;Stime.Core.Vnext.Hosting;[2.0.0,3.0.0);Reference\n", new UTF8Encoding(true));

            var (requests, skipped) = await VersionsFile.ReadAsync(path, CancellationToken.None);

            Assert.Equal([new PackageRequest("Stime.Core.Vnext.Messaging", "2.0.4"),
                new PackageRequest("Stime.Core.Vnext.Caching", "1.0.0-beta.25")], requests);
            Assert.Equal(3, skipped);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task VersionsFromAFileWithoutPackageAndVersionColumnsIsRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "Project;Repository;Branch\np;r;main\n");

            await Assert.ThrowsAsync<FormatException>(() => VersionsFile.ReadAsync(path, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    // --- resolution

    private static FakePackageFeed Feed() => new FakePackageFeed()
        .Add("Stime.Core.Vnext.Messaging", "2.0.0-beta.6", Nuspec("""<dependencies><group targetFramework="net6.0" /></dependencies>"""))
        .Add("Stime.Core.Vnext.Messaging", "2.0.0-beta.12", Nuspec("""<dependencies><group targetFramework="net6.0" /></dependencies>"""))
        .Add("Stime.Core.Vnext.Messaging", "2.0.4", Nuspec("""
            <dependencies>
              <group targetFramework="net8.0"><dependency id="Microsoft.Extensions.Hosting" version="8.0.0" /></group>
            </dependencies>
            <frameworkReferences>
              <group targetFramework="net8.0"><frameworkReference name="Microsoft.AspNetCore.App" /></group>
            </frameworkReferences>
            """))
        .Add("Stime.Core.Vnext.Messaging", "2.1.0-beta.6", Nuspec("""<dependencies><group targetFramework="net8.0" /></dependencies>"""))
        .Add("Stime.Core.Vnext.FeatureFlipping", "0.1.2", Nuspec(""), Nupkg("lib/netstandard2.0/F.dll"))
        .Add("Stime.Core.Vnext.Broken", "1.0.0", Nuspec(""))
        .Add("Newtonsoft.Json", "13.0.3", Nuspec("""<dependencies><group targetFramework="netstandard2.0" /></dependencies>"""))
        .Fail("Stime.Core.Vnext.Messaging", "2.0.0-beta.6")
        .Publish("Stime.Core.Vnext.Messaging", "2.0.4", new DateTimeOffset(2025, 3, 14, 9, 30, 0, TimeSpan.Zero), true, "Release")
        .Publish("Stime.Core.Vnext.Messaging", "2.1.0-BETA.6", new DateTimeOffset(2025, 6, 2, 0, 0, 0, TimeSpan.Zero), false)
        .FailPublications("Newtonsoft.Json");

    private static Task<FeedResolution> ResolveAsync(FakePackageFeed feed, string[] packages,
        string[]? patterns = null, bool latest = false) =>
        FeedScanner.ResolveAsync(feed, new FeedRequest(packages.Select(PackageRequest.Parse).ToArray(),
            patterns is null ? null : DependencyMatcher.Create(patterns, []), latest), CancellationToken.None);

    private static string[] Labels(FeedResolution resolution) =>
        resolution.Versions.Select(v => $"{v.Id} {v.Version}").ToArray();

    [Fact]
    public async Task APackageWithoutVersionGivesEveryVersionInSemanticOrder()
    {
        var resolution = await ResolveAsync(Feed(), ["Stime.Core.Vnext.Messaging"]);

        Assert.Equal(
        [
            "Stime.Core.Vnext.Messaging 2.0.0-beta.6",
            "Stime.Core.Vnext.Messaging 2.0.0-beta.12",
            "Stime.Core.Vnext.Messaging 2.0.4",
            "Stime.Core.Vnext.Messaging 2.1.0-beta.6"
        ], Labels(resolution));
    }

    [Fact]
    public async Task LatestKeepsTheHighestVersionPrereleasesIncludedButNotTheRequestedOnes()
    {
        var resolution = await ResolveAsync(Feed(),
            ["Stime.Core.Vnext.Messaging", "Stime.Core.Vnext.FeatureFlipping@0.1.2"], latest: true);

        Assert.Equal(["Stime.Core.Vnext.FeatureFlipping 0.1.2", "Stime.Core.Vnext.Messaging 2.1.0-beta.6"],
            Labels(resolution));
    }

    [Fact]
    public async Task APatternReadsTheMatchingPackagesOfTheFeedOnce()
    {
        var resolution = await ResolveAsync(Feed(),
            ["stime.core.vnext.messaging@2.0.4", "Stime.Core.Vnext.FeatureFlipping"], ["*.Vnext.*"], latest: true);

        Assert.Equal(
        [
            "Stime.Core.Vnext.Broken 1.0.0",
            "Stime.Core.Vnext.FeatureFlipping 0.1.2",
            "stime.core.vnext.messaging 2.0.4",
            "Stime.Core.Vnext.Messaging 2.1.0-beta.6"
        ], Labels(resolution));
        Assert.Empty(resolution.Unknown);
    }

    [Fact]
    public async Task ARequestedVersionAlsoFoundByAFilterKeepsItsSpelling()
    {
        var resolution = await ResolveAsync(Feed(), ["STIME.Core.Vnext.Messaging@2.0.4"], ["*.Messaging"]);

        Assert.Contains("STIME.Core.Vnext.Messaging 2.0.4", Labels(resolution));
        Assert.Equal(4, resolution.Versions.Count);
    }

    [Fact]
    public async Task AnUnknownPackageIsReportedNotFound()
    {
        var resolution = await ResolveAsync(Feed(), ["Stime.Core.Vnext.Missing"]);

        Assert.Empty(resolution.Versions);
        Assert.Equal([new PackageInfo("Stime.Core.Vnext.Missing", "", PackageStatus.NotFound,
            "Package not found in the feed")], resolution.Unknown);
    }

    // --- reading

    private static async Task<FeedScanResult> ReadAsync(string[] packages, string[]? dependencyPatterns = null,
        int parallelism = 1)
    {
        var feed = Feed();
        var resolution = await ResolveAsync(feed, packages);
        return await FeedScanner.ReadAsync(feed, resolution,
            dependencyPatterns is null ? null : DependencyMatcher.Create(dependencyPatterns, []),
            parallelism, null, CancellationToken.None);
    }

    [Fact]
    public async Task EachVersionGivesItsFrameworksAndDependencies()
    {
        var result = await ReadAsync(["Stime.Core.Vnext.Messaging@2.0.4"]);

        Assert.Equal([new PackageFramework("Stime.Core.Vnext.Messaging", "2.0.4", "net8.0")], result.Frameworks);
        Assert.Equal(
        [
            new PackageDependency("Stime.Core.Vnext.Messaging", "2.0.4", "net8.0", "Microsoft.Extensions.Hosting",
                "8.0.0", DependencyKind.Package),
            new PackageDependency("Stime.Core.Vnext.Messaging", "2.0.4", "net8.0", "Microsoft.AspNetCore.App",
                null, DependencyKind.FrameworkReference)
        ], result.Dependencies);
        var info = Assert.Single(result.Packages);
        Assert.Equal(PackageStatus.OK, info.Status);
        Assert.Null(info.Details);
        Assert.Equal(FrameworksFrom.Nuspec, info.FrameworksFrom);
        Assert.Equal("X", info.Metadata?.Id);
    }

    [Fact]
    public async Task DependencyFiltersKeepTheMatchingDependenciesOnly()
    {
        var result = await ReadAsync(["Stime.Core.Vnext.Messaging@2.0.4"], ["Microsoft.AspNetCore.*"]);

        Assert.Equal("Microsoft.AspNetCore.App", Assert.Single(result.Dependencies).Dependency);
        Assert.Single(result.Frameworks);
        Assert.Equal(2, Assert.Single(result.Diagnostics).Dependencies.Count);
    }

    [Fact]
    public async Task EachVersionGivesItsPublication()
    {
        var result = await ReadAsync(["Stime.Core.Vnext.Messaging"]);

        Assert.Equal(
        [
            "2.0.0-beta.6   ",
            "2.0.0-beta.12   ",
            "2.0.4 2025-03-14 True Release",
            "2.1.0-beta.6 2025-06-02 False "
        ], result.Packages.Select(x =>
            $"{x.Version} {x.Published:yyyy-MM-dd} {x.Listed} {string.Join(",", x.Views ?? [])}"));
    }

    [Fact]
    public async Task AnUnknownPublicationIsReportedWithoutFailingTheVersion()
    {
        var result = await ReadAsync(["Newtonsoft.Json@13.0.3"]);

        var info = Assert.Single(result.Packages);
        Assert.Equal(PackageStatus.OK, info.Status);
        Assert.Equal("Publication unknown: feed API unavailable", info.Details);
        Assert.Single(result.Frameworks);
    }

    [Fact]
    public async Task APackageWithoutDependencyGroupsGivesTheFrameworksOfItsFolders()
    {
        var result = await ReadAsync(["Stime.Core.Vnext.FeatureFlipping@0.1.2"]);

        Assert.Equal("netstandard2.0", Assert.Single(result.Frameworks).Framework);
        Assert.Equal(FrameworksFrom.PackageFolders, Assert.Single(result.Packages).FrameworksFrom);
        Assert.Equal(FrameworksFrom.PackageFolders, Assert.Single(result.Diagnostics).FrameworksFrom);
    }

    [Fact]
    public async Task VersionsThatCannotBeReadAreReportedWithoutStoppingTheScan()
    {
        var result = await ReadAsync(["Stime.Core.Vnext.Missing", "Stime.Core.Vnext.Messaging@9.9.9",
            "Stime.Core.Vnext.Messaging@2.0.0-beta.6", "Stime.Core.Vnext.Broken@1.0.0", "Newtonsoft.Json@13.0.3"]);

        Assert.Equal(
        [
            "Stime.Core.Vnext.Missing  NotFound",
            "Newtonsoft.Json 13.0.3 OK",
            "Stime.Core.Vnext.Broken 1.0.0 Failed",
            "Stime.Core.Vnext.Messaging 2.0.0-beta.6 Failed",
            "Stime.Core.Vnext.Messaging 9.9.9 NotFound"
        ], result.Packages.Select(x => $"{x.Package} {x.Version} {x.Status}"));
        Assert.Equal("The package file is missing from the feed.", result.Packages[2].Details);
        Assert.Equal(2, result.Failed);
        Assert.Equal(2, result.NotFound);
    }

    [Theory]
    [InlineData(0, "Stime.Core.Vnext.Messaging@2.0.4")]
    [InlineData(1, "Stime.Core.Vnext.Missing")]
    [InlineData(1, "Stime.Core.Vnext.Messaging@9.9.9")]
    [InlineData(1, "Stime.Core.Vnext.Broken@1.0.0")]
    public async Task AnyPackageOrVersionNotFoundOrNotReadFailsTheCommand(int exitCode, string package)
    {
        var result = await ReadAsync(["Stime.Core.Vnext.Messaging@2.0.4", package]);

        Assert.Equal(exitCode, result.ExitCode);
    }

    [Fact]
    public async Task ParallelReadsGiveTheSameResult()
    {
        string[] packages = ["Stime.Core.Vnext.Messaging", "Stime.Core.Vnext.FeatureFlipping", "Newtonsoft.Json"];

        var sequential = await ReadAsync(packages);
        var parallel = await ReadAsync(packages, parallelism: 4);

        Assert.Equal(sequential.Frameworks, parallel.Frameworks);
        Assert.Equal(sequential.Dependencies, parallel.Dependencies);
        // Views are lists, compared by reference in records: the packages are compared through their text.
        Assert.Equal(sequential.Packages.Select(p => $"{p} {string.Join(",", p.Views ?? [])}"),
            parallel.Packages.Select(p => $"{p} {string.Join(",", p.Views ?? [])}"));
    }

    // --- report

    [Fact]
    public async Task FeedReportsWriteFrameworksDependenciesAndCoverage()
    {
        var metadata = new PackageMetadata("Stime.Core.Vnext.Messaging", null, "Stime", null, "Line one;\nline two",
            null, null, null, null, null, null, null, null, null, "MIT", "expression", null, null, null, null, null,
            null, "git", "https://dev.azure.com/acme/_git/messaging", null, "abc123");
        var report = new FeedReport(DateTimeOffset.UnixEpoch, "https://feed", [], [],
            [new("Stime.Core.Vnext.Messaging", "2.0.4", "net8.0")],
            [new("Stime.Core.Vnext.Messaging", "2.0.4", "net8.0", "Microsoft.AspNetCore.App", null, DependencyKind.FrameworkReference)],
            [
                new("Stime.Core.Vnext.Missing", "", PackageStatus.NotFound, "Package not found in the feed"),
                new("Stime.Core.Vnext.Messaging", "2.0.4", PackageStatus.OK, null, FrameworksFrom.Nuspec,
                    new DateTimeOffset(2025, 3, 14, 9, 30, 0, TimeSpan.Zero), true, ["Release", "Prerelease"], metadata)
            ]);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await ReportWriter.SaveFeedAsync(report, directory, CancellationToken.None);

            Assert.Equal(["Package;Version;Framework", "Stime.Core.Vnext.Messaging;2.0.4;net8.0"],
                await File.ReadAllLinesAsync(Path.Combine(directory, "package-frameworks.csv")));
            Assert.Equal(["Package;Version;Framework;Dependency;VersionRange;Kind",
                "Stime.Core.Vnext.Messaging;2.0.4;net8.0;Microsoft.AspNetCore.App;;FrameworkReference"],
                await File.ReadAllLinesAsync(Path.Combine(directory, "package-dependencies.csv")));
            var packages = VersionsFile.ParseCsv(await File.ReadAllTextAsync(Path.Combine(directory, "packages.csv")));
            Assert.Equal(34, packages[0].Length);
            Assert.Equal(["Package", "Version", "Status", "Details", "FrameworksFrom", "Published", "Listed", "Views",
                "Id", "Title", "Authors"], packages[0].Take(11));
            Assert.Equal(["Stime.Core.Vnext.Missing", "", "NotFound", "Package not found in the feed", "", "", "", ""],
                packages[1].Take(8));
            Assert.Equal(["Stime.Core.Vnext.Messaging", "2.0.4", "OK", "", "Nuspec", "2025-03-14T09:30:00Z", "true",
                "Release | Prerelease", "Stime.Core.Vnext.Messaging", "", "Stime", "", "Line one;\nline two"],
                packages[2].Take(13));
            Assert.Equal(["MIT", "expression"], packages[2].Skip(22).Take(2));
            Assert.Equal(["git", "https://dev.azure.com/acme/_git/messaging", "", "abc123"], packages[2].Skip(30));
            Assert.Contains("\"Kind\": \"FrameworkReference\"",
                await File.ReadAllTextAsync(Path.Combine(directory, "report.json")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // --- Azure Artifacts client

    private const string Index = "https://pkgs.dev.azure.com/acme/_packaging/feed/nuget/v3/index.json";
    private const string Base = "https://pkgs.dev.azure.com/acme/_packaging/abc/nuget/v3/flat2/";

    private static (AzureArtifactsFeed Feed, StubServer Server) Client(Dictionary<string, (HttpStatusCode, string)> responses)
    {
        var server = new StubServer(responses);
        var feed = new AzureArtifactsFeed(new AzureFeed("acme", null, "feed"), new HttpClient(server));
        return (feed, server);
    }

    private static (HttpStatusCode, string) Ok(string body) => (HttpStatusCode.OK, body);

    private static readonly (HttpStatusCode, string) IndexResponse =
        Ok($$"""{"resources":[{"@id":"{{Base}}","@type":"PackageBaseAddress/3.0.0"}]}""");

    [Fact]
    public async Task TheClientReadsLowercaseIdsAndVersionsAndReadsTheIndexOnce()
    {
        var (feed, server) = Client(new()
        {
            [Index] = IndexResponse,
            [Base + "stime.core.vnext.messaging/index.json"] = Ok("""{"versions":["2.0.4","2.1.0-beta.6"]}"""),
            [Base + "stime.core.vnext.messaging/2.1.0-beta.6/stime.core.vnext.messaging.nuspec"] = Ok("<package />")
        });
        using (feed)
        {
            Assert.Equal(["2.0.4", "2.1.0-beta.6"], await feed.GetVersionsAsync("Stime.Core.Vnext.Messaging", default));
            Assert.Equal("<package />", await feed.GetNuspecAsync("Stime.Core.Vnext.Messaging", "2.1.0-BETA.6", default));
            Assert.Equal(1, server.Requests.Count(r => r == Index));
            Assert.All(server.Methods, m => Assert.Equal(HttpMethod.Get, m));
        }
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("2.0.0.0")]
    [InlineData("2.0.0+build.42")]
    public async Task TheClientRequestsNormalizedVersions(string version)
    {
        var (feed, server) = Client(new()
        {
            [Index] = IndexResponse,
            [Base + "a/2.0.0/a.nuspec"] = Ok("<package />"),
            [Base + "a/2.0.0/a.2.0.0.nupkg"] = Ok("zip")
        });
        using (feed)
        {
            Assert.Equal("<package />", await feed.GetNuspecAsync("A", version, default));
            Assert.NotNull(await feed.GetPackageAsync("A", version, default));
        }
    }

    [Fact]
    public async Task TheClientReportsAnUnknownPackageOrVersionAsNull()
    {
        var (feed, _) = Client(new() { [Index] = IndexResponse });
        using (feed)
        {
            Assert.Null(await feed.GetVersionsAsync("Missing", default));
            Assert.Null(await feed.GetNuspecAsync("Missing", "1.0.0", default));
            Assert.Null(await feed.GetPackageAsync("Missing", "1.0.0", default));
        }
    }

    [Fact]
    public async Task TheClientNamesThePackagingScopeWhenAccessIsDenied()
    {
        var (feed, _) = Client(new() { [Index] = (HttpStatusCode.Unauthorized, "") });
        using (feed)
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => feed.GetVersionsAsync("A", default));
            Assert.Contains("Packaging: Read", error.Message);
        }
    }

    [Fact]
    public async Task TheClientReadsThePublicationsOfThePackageNamedExactly()
    {
        const string api = "https://feeds.dev.azure.com/acme/_apis/packaging/Feeds/feed/packages?protocolType=NuGet&packageNameQuery=Stime.Core.Vnext.Messaging&includeAllVersions=true";
        var (feed, server) = Client(new()
        {
            // Prereleases and delisted versions are only returned by the filtered requests.
            [api + "&isRelease=false&api-version=7.1"] = Ok("""
                {"value":[{"name":"Stime.Core.Vnext.Messaging","versions":[
                  {"normalizedVersion":"2.1.0-beta.6","isListed":true},
                  {"normalizedVersion":"2.0.4","isListed":true}
                ]}]}
                """),
            [api + "&isRelease=false&isListed=false&api-version=7.1"] = Ok("""
                {"value":[{"name":"Stime.Core.Vnext.Messaging","versions":[{"normalizedVersion":"2.0.0-beta.6","isListed":false}]}]}
                """),
            [api + "&api-version=7.1"] = Ok("""
                {"value":[
                  {"name":"Stime.Core.Vnext.Messaging.Extensions","versions":[{"normalizedVersion":"9.9.9"}]},
                  {"name":"Stime.Core.Vnext.Messaging","versions":[
                    {"normalizedVersion":"2.0.4","publishDate":"2025-03-14T09:30:00.123Z","isListed":true,"views":[{"name":"Release"},{"name":"Prerelease"}]},
                    {"normalizedVersion":"2.1.0-beta.6","isListed":false}
                  ]}
                ]}
                """)
        });
        using (feed)
        {
            var publications = await feed.GetPublicationsAsync("Stime.Core.Vnext.Messaging", default);

            Assert.Equal(["2.0.4 True", "2.1.0-beta.6 False", "2.0.0-beta.6 False"],
                publications.Select(p => $"{p.Version} {p.Listed}"));
            Assert.Equal(new DateTimeOffset(2025, 3, 14, 9, 30, 0, 123, TimeSpan.Zero), publications[0].Published);
            Assert.Equal(["Release", "Prerelease"], publications[0].Views);
            Assert.Empty(publications[1].Views);
            Assert.Equal(4, server.Requests.Count);
            Assert.Empty(await feed.GetPublicationsAsync("Unknown", default));
        }
    }

    [Fact]
    public async Task TheClientListsThePackageIdsPageByPage()
    {
        const string api = "https://feeds.dev.azure.com/acme/_apis/packaging/Feeds/feed/packages?protocolType=NuGet&%24top=1000&%24skip=";
        var firstPage = string.Join(",", Enumerable.Range(0, 1000).Select(i => $$"""{"name":"P{{i}}"}"""));
        var (feed, server) = Client(new()
        {
            [api + "0&api-version=7.1"] = Ok($$"""{"value":[{{firstPage}}]}"""),
            [api + "1000&api-version=7.1"] = Ok("""{"value":[{"name":"Last"}]}"""),
            // A package whose versions are all prereleases is only listed with isRelease=false.
            [api + "0&isRelease=false&api-version=7.1"] = Ok("""{"value":[{"name":"P1"},{"name":"Stime.Core.Vnext.Caching"}]}"""),
            [api + "0&isListed=false&api-version=7.1"] = Ok("""{"value":[]}"""),
            [api + "0&isRelease=false&isListed=false&api-version=7.1"] = Ok("""{"value":[]}""")
        });
        using (feed)
        {
            var ids = await feed.GetPackageIdsAsync(default);

            Assert.Equal(1002, ids.Count);
            Assert.Equal(["Last", "Stime.Core.Vnext.Caching"], ids.TakeLast(2));
            Assert.Equal(5, server.Requests.Count);
        }
    }

    /// <summary>Answers from a fixed table of URLs; any other URL is a 404.</summary>
    private sealed class StubServer(Dictionary<string, (HttpStatusCode Status, string Body)> responses) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests)
            {
                Requests.Add(url);
                Methods.Add(request.Method);
            }
            var (status, body) = responses.TryGetValue(url, out var response) ? response : (HttpStatusCode.NotFound, "");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
