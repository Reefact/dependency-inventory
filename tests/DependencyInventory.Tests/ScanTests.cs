using DependencyInventory;
using Xunit;

namespace DependencyInventory.Tests;

public sealed class ScanTests
{
    [Fact]
    public void ParserExtractsAllPackagesAndDistinguishesCentralDeclarations()
    {
        const string xml = """
            <Project><ItemGroup>
                <PackageReference Include="Stime.Core.Hosting" Version="2.0.0" />
                <PackageReference Include="Serilog" Version="4.0.0" />
                <PackageVersion Include="Stime.Core.Logging" Version="3.0.0" />
            </ItemGroup></Project>
            """;

        var packages = NuGetDependencyParser.Parse("/src/Directory.Packages.props", xml);
        Assert.Equal(3, packages.Count);
        Assert.Equal(PackageKind.Reference, packages[0].Kind);
        Assert.Equal(PackageKind.CentralVersion, packages[2].Kind);
    }

    [Fact]
    public void ParserReadsPackageReferencesInCsproj()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
                <PackageReference Include="Stime.Core.FeatureFlag" Version="1.0.0" />
                <PackageReference Include="Stime.Core.Infrastructure.Hosting" Version="2.0.0" />
            </ItemGroup></Project>
            """;

        var packages = NuGetDependencyParser.Parse("/src/Api.Order.Bo.csproj", xml);
        Assert.Equal(2, packages.Count);
        Assert.Equal("Stime.Core.FeatureFlag", packages[0].Id);
        Assert.Equal("Stime.Core.Infrastructure.Hosting", packages[1].Id);
    }

    [Fact]
    public void ParserReadsPackagesConfig()
    {
        const string xml = """
            <packages><package id="Serilog" version="4.0.0" /></packages>
            """;
        var parsed = NuGetDependencyParser.Parse("/packages.config", xml);
        Assert.Single(parsed);
        Assert.Equal("4.0.0", parsed[0].Version);
    }

    [Theory]
    [InlineData("Stime.*", "Stime.Core.Logging", true)]
    [InlineData("Stime.*", "STIME.Core.Logging", true)]
    [InlineData("Stime.*", "Other.Stime.Core", false)]
    [InlineData("*.Logging", "Stime.Core.Logging", true)]
    [InlineData("*.Logging", "Stime.Core.Logging.Extra", false)]
    [InlineData("Serilog.?", "Serilog.A", true)]
    [InlineData("Serilog.?", "Serilog.AB", false)]
    public void GlobMatchesWholePackageId(
        string pattern, string package, bool expected)
    {
        var matcher = DependencyMatcher.Create([pattern], []);
        Assert.Equal(expected, matcher.Match(package).Count > 0);
    }

    [Fact]
    public void RegexUsesDotNetSearchSemantics()
    {
        var matcher = DependencyMatcher.Create([], [@"Core\.Infrastructure"]);
        Assert.Single(matcher.Match("Stime.Core.Infrastructure.Hosting"));
        Assert.Empty(matcher.Match("Stime.Core.Hosting"));
    }

    [Fact]
    public void GlobAndRegexAreCombinedWithOr()
    {
        var matcher = DependencyMatcher.Create(
            ["Stime.*", "Microsoft.Extensions.*"], [@"^Serilog(\.|$)"]);
        Assert.Single(matcher.Match("Stime.Core.Logging"));
        Assert.Single(matcher.Match("Microsoft.Extensions.Http"));
        Assert.Single(matcher.Match("Serilog.Sinks.Console"));
        Assert.Empty(matcher.Match("Newtonsoft.Json"));
        Assert.Equal(3, matcher.Filters.Count);
    }

    [Fact]
    public void MatchingMultipleFiltersDoesNotDuplicatePackage()
    {
        var matcher = DependencyMatcher.Create(["Stime.*"], [@"^Stime\.Core"]);
        var matches = matcher.Match("Stime.Core.Hosting");
        Assert.Equal(2, matches.Count);
        Assert.Contains("Glob:Stime.*", matches);
        Assert.Contains(@"Regex:^Stime\.Core", matches);
    }

    [Fact]
    public void CaseSensitiveModeCanBeEnabled()
    {
        var matcher = DependencyMatcher.Create(["Stime.*"], [], caseSensitive: true);
        Assert.Single(matcher.Match("Stime.Core"));
        Assert.Empty(matcher.Match("stime.core"));
    }

    [Fact]
    public void InvalidAndMissingFiltersFailEarly()
    {
        Assert.Throws<ArgumentException>(() => DependencyMatcher.Create([], []));
        Assert.Throws<ArgumentException>(() => DependencyMatcher.Create([], ["["]));
        Assert.Throws<ArgumentException>(() => DependencyMatcher.Create([""], []));
    }

    [Fact]
    public void CentralVersionOnlyUsesPropsOfFoldersOfTheSameCase()
    {
        var files = new Dictionary<string, IReadOnlyList<ParsedPackage>>
        {
            ["/Directory.Packages.props"] = [new("Stime.Core.Logging", "1.0", PackageKind.CentralVersion, VersionSource.Explicit)],
            ["/src/App/Directory.Packages.props"] = [new("Stime.Core.Logging", "2.0", PackageKind.CentralVersion, VersionSource.Explicit)]
        };
        Assert.Equal("1.0", NuGetDependencyParser.ResolveCentralVersion(
            "/src/app/Api.csproj", "Stime.Core.Logging", files));
    }

    [Fact]
    public void CentralVersionOnlyUsesNearestProps()
    {
        var files = new Dictionary<string, IReadOnlyList<ParsedPackage>>
        {
            ["/Directory.Packages.props"] = [new("Stime.Core.Logging", "1.0", PackageKind.CentralVersion, VersionSource.Explicit)],
            ["/src/Directory.Packages.props"] = [new("Stime.Core.Logging", "2.0", PackageKind.CentralVersion, VersionSource.Explicit)]
        };
        Assert.Equal("2.0", NuGetDependencyParser.ResolveCentralVersion(
            "/src/Api/Api.csproj", "Stime.Core.Logging", files));
    }

    [Theory]
    [InlineData("/src/Api/Api.csproj", true)]
    [InlineData("/src/Api/Api.FSPROJ", true)]
    [InlineData(@"src\Legacy\Legacy.vbproj", true)]
    [InlineData("/Directory.Packages.props", false)]
    [InlineData("/packages.config", false)]
    [InlineData("/src/Api/Api.csproj.user", false)]
    public void IsProjectFileRecognizesProjectExtensions(string path, bool expected)
    {
        Assert.Equal(expected, NuGetDependencyParser.IsProjectFile(path));
    }

    [Fact]
    public async Task ReportsWriteEnumNamesAsText()
    {
        var report = new ScanReport(DateTimeOffset.UnixEpoch, "https://dev.azure.com/acme/project",
            [new DependencyFilter("Glob", "Stime.*")],
            [
                new("project", "api", "main", "abc", "/src/Api.csproj", "Stime.Web", "2.0.0",
                    PackageKind.Reference, VersionSource.Central, ["Glob:Stime.*"], "https://example/web"),
                new("project", "api", "main", "abc", "/Directory.Packages.props", "Stime.Web", "2.0.0",
                    PackageKind.CentralVersion, VersionSource.Explicit, ["Glob:Stime.*"], "https://example/props")
            ],
            [new("project", "api", "main", CoverageStatus.Partial, 1, 1, null)]);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await ReportWriter.SaveAsync(report, directory, CancellationToken.None);

            // The CSV files keep one kind each: the Kind is only written to report.json.
            Assert.Contains(",2.0.0,Central,",
                await File.ReadAllTextAsync(Path.Combine(directory, "dependencies.csv")));
            Assert.Contains(",2.0.0,Explicit,",
                await File.ReadAllTextAsync(Path.Combine(directory, "central-versions.csv")));
            Assert.Contains(",Partial,",
                await File.ReadAllTextAsync(Path.Combine(directory, "repositories.csv")));
            var json = await File.ReadAllTextAsync(Path.Combine(directory, "report.json"));
            Assert.Contains("\"Kind\": \"CentralVersion\"", json);
            Assert.Contains("\"VersionSource\": \"Central\"", json);
            Assert.Contains("\"Status\": \"Partial\"", json);
            Assert.Contains("\"Scope\": \"project\"", json);
            Assert.DoesNotContain("\"Project\":", json);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(",", ',')]
    [InlineData(";", ';')]
    [InlineData("tab", '\t')]
    [InlineData("TAB", '\t')]
    [InlineData("\\t", '\t')]
    [InlineData("\t", '\t')]
    public void TheCsvDelimiterIsACommaASemicolonOrATab(string name, char expected)
    {
        Assert.Equal(expected, CsvFormat.Delimiter(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("|")]
    [InlineData(" ;")]
    public void AnyOtherCsvDelimiterIsRefused(string name)
    {
        Assert.Throws<ArgumentException>(() => CsvFormat.Delimiter(name));
    }

    [Theory]
    [InlineData(',', "project,api,,,,Failed,0,0,\"Timeout, then 503\",")]
    [InlineData(';', "project;api;;;;Failed;0;0;Timeout, then 503;")]
    [InlineData('\t', "project\tapi\t\t\t\tFailed\t0\t0\tTimeout, then 503\t")]
    public async Task ReportsUseTheSeparatorAndQuoteTheValuesHoldingIt(char delimiter, string line)
    {
        var report = new ScanReport(DateTimeOffset.UnixEpoch, "origin", [], [],
            [new("project", "api", null, CoverageStatus.Failed, 0, 0, "Timeout, then 503")]);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await ReportWriter.SaveAsync(report, directory, false, delimiter, CancellationToken.None);

            var lines = await File.ReadAllLinesAsync(Path.Combine(directory, "repositories.csv"));
            Assert.Equal(string.Join(delimiter, "Scope", "Repository", "Branch", "Commit", "CommitDate", "Status",
                "FilesExamined", "References", "Details", "Url"), lines[0]);
            Assert.Equal(line, lines[1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UsageColumnsAreWrittenToDependenciesOnlyAndUsedTypesAreOnlyWrittenOnRequest()
    {
        PackageOccurrence Occurrence(PackageKind kind, string file) =>
            new("project", "api", "main", "abc", file, "Stime.Web", "2.0.0", kind, VersionSource.Explicit,
                ["Glob:Stime.*"], "https://example/web",
                kind == PackageKind.Reference ? PackageUsage.Used : null,
                kind == PackageKind.Reference ? PackageSource.Feed : null,
                kind == PackageKind.Reference ? "Shared/libs" : null,
                kind == PackageKind.Reference ? [new UsedType("Stime.Web.Host", 3)] : null,
                kind == PackageKind.Reference ? ProjectType.Application : null);
        var report = new ScanReport(DateTimeOffset.UnixEpoch, "https://dev.azure.com/acme/project", [],
            [Occurrence(PackageKind.Reference, "/src/Api.csproj"),
                Occurrence(PackageKind.CentralVersion, "/Directory.Packages.props")], []);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await ReportWriter.SaveAsync(report, directory, false, CancellationToken.None);
            Assert.False(File.Exists(Path.Combine(directory, "used-types.csv")));
            await ReportWriter.SaveAsync(report, directory, true, CancellationToken.None);

            Assert.Equal(
            [
                "Scope,Repository,Branch,Commit,File,ProjectType,Package,Version,VersionSource,MatchedFilters,Usage,Source,Feed,Url",
                "project,api,main,abc,/src/Api.csproj,Application,Stime.Web,2.0.0,Explicit,Glob:Stime.*,Used,Feed,Shared/libs,https://example/web"
            ], await File.ReadAllLinesAsync(Path.Combine(directory, "dependencies.csv")));
            Assert.Equal("Scope,Repository,Branch,Commit,File,Package,Version,VersionSource,MatchedFilters,Url",
                (await File.ReadAllLinesAsync(Path.Combine(directory, "central-versions.csv")))[0]);
            Assert.Equal(
            [
                "Scope,Repository,Branch,Commit,File,Package,Version,Source,Type,Files",
                "project,api,main,abc,/src/Api.csproj,Stime.Web,2.0.0,Feed,Stime.Web.Host,3"
            ], await File.ReadAllLinesAsync(Path.Combine(directory, "used-types.csv")));

            // A later run without --used-types in the same folder leaves no stale file behind.
            await ReportWriter.SaveAsync(report, directory, false, CancellationToken.None);
            Assert.False(File.Exists(Path.Combine(directory, "used-types.csv")));
            var json = await File.ReadAllTextAsync(Path.Combine(directory, "report.json"));
            Assert.Contains("\"Usage\": \"Used\"", json);
            Assert.Contains("\"Source\": \"Feed\"", json);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ParsedPackageSerializesEnumNamesAsText()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new ParsedPackage("Serilog", null, PackageKind.Reference, VersionSource.Unresolved));
        Assert.Contains("\"Kind\":\"Reference\"", json);
        Assert.Contains("\"VersionSource\":\"Unresolved\"", json);
    }

    [Theory]
    [InlineData(SourceProvider.Azure, null, ".azure-credentials")]
    [InlineData(SourceProvider.GitLab, null, ".gitlab-credentials")]
    [InlineData(SourceProvider.Azure, "custom.auth", "custom.auth")]
    [InlineData(SourceProvider.GitLab, "secrets/gitlab.txt", "secrets/gitlab.txt")]
    public void CredentialsFileUsesProviderDefaultOrExplicitPath(
        SourceProvider provider, string? path, string expected)
    {
        Assert.Equal(expected, CredentialsFile.ResolvePath(provider, path));
    }

    [Theory]
    [InlineData("alice@company.com\npat-token", "alice@company.com", "pat-token")]
    [InlineData("alice@company.com\r\npat-token\r\n", "alice@company.com", "pat-token")]
    [InlineData("\uFEFFalice@company.com\npat-token", "alice@company.com", "pat-token")]
    public void AzureCredentialsAreTwoLines(
        string contents, string expectedLogin, string expectedPat)
    {
        var credentials = CredentialsFile.Parse(contents, SourceProvider.Azure);
        Assert.Equal(expectedLogin, credentials.Login);
        Assert.Equal(expectedPat, credentials.Pat);
    }

    [Theory]
    [InlineData("glpat-token")]
    [InlineData("\uFEFFglpat-token\n")]
    public void GitLabCredentialsAreOneLine(string contents)
    {
        var credentials = CredentialsFile.Parse(contents, SourceProvider.GitLab);
        Assert.Equal("", credentials.Login);
        Assert.Equal("glpat-token", credentials.Pat);
    }

    [Theory]
    [InlineData(SourceProvider.Azure, "")]
    [InlineData(SourceProvider.Azure, "alice@company.com\n")]
    [InlineData(SourceProvider.Azure, "pat-token")]
    [InlineData(SourceProvider.GitLab, "")]
    [InlineData(SourceProvider.GitLab, "alice@company.com\npat-token")]
    public void InvalidCredentialsAreRejected(SourceProvider provider, string contents)
    {
        Assert.Throws<FormatException>(() => CredentialsFile.Parse(contents, provider));
    }

    [Fact]
    public void GitLabOriginSupportsNestedGroups()
    {
        var origin = GitLabOrigin.Parse("https://gitlab.example.com/red/web");
        Assert.Equal("https://gitlab.example.com", origin.BaseUrl);
        Assert.Equal("red/web", origin.GroupPath);
    }

    [Theory]
    [InlineData("https://gitlab.example.com/")]
    [InlineData("http://gitlab.example.com/team")]
    [InlineData("https://gitlab.example.com/api/v4")]
    public void GitLabOriginRejectsInvalidUrls(string url)
    {
        Assert.Throws<ArgumentException>(() => GitLabOrigin.Parse(url));
    }

    [Fact]
    public void OriginRejectsRepositoryUrl()
    {
        Assert.Throws<ArgumentException>(() =>
            AzureOrigin.Parse("https://dev.azure.com/acme/project/_git/repo"));
        Assert.Equal("acme", AzureOrigin.Parse("https://dev.azure.com/acme/project").Organization);
    }
}
