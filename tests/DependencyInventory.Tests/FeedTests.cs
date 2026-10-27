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
        .Fail("Stime.Core.Vnext.Messaging", "2.0.0-beta.6");

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
        Assert.Equal([new PackageCoverage("Stime.Core.Vnext.Missing", "", PackageStatus.NotFound,
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
        Assert.Equal([new PackageCoverage("Stime.Core.Vnext.Messaging", "2.0.4", PackageStatus.OK, null)],
            result.Packages);
    }

    [Fact]
    public async Task DependencyFiltersKeepTheMatchingDependenciesOnly()
    {
        var result = await ReadAsync(["Stime.Core.Vnext.Messaging@2.0.4"], ["Microsoft.AspNetCore.*"]);

        Assert.Equal("Microsoft.AspNetCore.App", Assert.Single(result.Dependencies).Dependency);
        Assert.Single(result.Frameworks);
    }

    [Fact]
    public async Task APackageWithoutDependencyGroupsGivesTheFrameworksOfItsFolders()
    {
        var result = await ReadAsync(["Stime.Core.Vnext.FeatureFlipping@0.1.2"]);

        Assert.Equal("netstandard2.0", Assert.Single(result.Frameworks).Framework);
        Assert.True(Assert.Single(result.Diagnostics).FrameworksFromPackageFolders);
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

    [Fact]
    public async Task ParallelReadsGiveTheSameResult()
    {
        string[] packages = ["Stime.Core.Vnext.Messaging", "Stime.Core.Vnext.FeatureFlipping", "Newtonsoft.Json"];

        var sequential = await ReadAsync(packages);
        var parallel = await ReadAsync(packages, parallelism: 4);

        Assert.Equal(sequential.Frameworks, parallel.Frameworks);
        Assert.Equal(sequential.Packages, parallel.Packages);
    }

    // --- report

    [Fact]
    public async Task FeedReportsWriteFrameworksDependenciesAndCoverage()
    {
        var report = new FeedReport(DateTimeOffset.UnixEpoch, "https://feed", [], [],
            [new("Stime.Core.Vnext.Messaging", "2.0.4", "net8.0")],
            [new("Stime.Core.Vnext.Messaging", "2.0.4", "net8.0", "Microsoft.AspNetCore.App", null, DependencyKind.FrameworkReference)],
            [new("Stime.Core.Vnext.Missing", "", PackageStatus.NotFound, "Package not found in the feed")]);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await ReportWriter.SaveFeedAsync(report, directory, CancellationToken.None);

            Assert.Equal(["Package;Version;Framework", "Stime.Core.Vnext.Messaging;2.0.4;net8.0"],
                await File.ReadAllLinesAsync(Path.Combine(directory, "package-frameworks.csv")));
            Assert.Equal(["Package;Version;Framework;Dependency;VersionRange;Kind",
                "Stime.Core.Vnext.Messaging;2.0.4;net8.0;Microsoft.AspNetCore.App;;FrameworkReference"],
                await File.ReadAllLinesAsync(Path.Combine(directory, "package-dependencies.csv")));
            Assert.Equal(["Package;Version;Status;Details", "Stime.Core.Vnext.Missing;;NotFound;Package not found in the feed"],
                await File.ReadAllLinesAsync(Path.Combine(directory, "packages.csv")));
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
    public async Task TheClientListsThePackageIdsPageByPage()
    {
        const string api = "https://feeds.dev.azure.com/acme/_apis/packaging/Feeds/feed/packages?protocolType=NuGet&%24top=1000&api-version=7.1&%24skip=";
        var firstPage = string.Join(",", Enumerable.Range(0, 1000).Select(i => $$"""{"name":"P{{i}}"}"""));
        var (feed, server) = Client(new()
        {
            [api.Replace("&api-version=7.1&%24skip=", "&%24skip=0&api-version=7.1")] = Ok($$"""{"value":[{{firstPage}}]}"""),
            [api.Replace("&api-version=7.1&%24skip=", "&%24skip=1000&api-version=7.1")] = Ok("""{"value":[{"name":"Last"}]}""")
        });
        using (feed)
        {
            var ids = await feed.GetPackageIdsAsync(default);

            Assert.Equal(1001, ids.Count);
            Assert.Equal("Last", ids[^1]);
            Assert.Equal(2, server.Requests.Count);
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
