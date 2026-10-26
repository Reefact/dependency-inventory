using DependencyInventory;
using Xunit;

namespace DependencyInventory.Tests;

public sealed class ScanTests
{
    [Theory]
    [InlineData("main", "release,dev,main,develop")]
    [InlineData("release", "develop,release,dev")]
    [InlineData("develop", "dev,develop,feature/z")]
    [InlineData("dev", "feature/z,dev,feature/a")]
    public void BranchSelectorPicksPriority(string expected, string namesCsv)
    {
        var branches = namesCsv.Split(',').Select(name => new GitBranch(name, "aaaa"));
        Assert.Equal(expected, BranchSelector.Select(branches)?.Name);
    }

    [Fact]
    public void ReleasePrefixBeatsDevelop()
    {
        var names = new[] { "feature/x", "develop", "release/2.0" };
        Assert.Equal("release/2.0", BranchSelector.Select(
            names.Select(x => new GitBranch(x, "aaaa")))?.Name);
    }

    [Fact]
    public void ArbitraryFallbackIsDeterministic()
    {
        var names = new[] { "feature/z", "feature/a" };
        Assert.Equal("feature/a", BranchSelector.Select(
            names.Select(x => new GitBranch(x, "aaaa")))?.Name);
    }

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

            Assert.Contains(";Reference;Central;",
                await File.ReadAllTextAsync(Path.Combine(directory, "dependencies.csv")));
            Assert.Contains(";CentralVersion;Explicit;",
                await File.ReadAllTextAsync(Path.Combine(directory, "central-versions.csv")));
            Assert.Contains(";Partial;",
                await File.ReadAllTextAsync(Path.Combine(directory, "repositories.csv")));
            var json = await File.ReadAllTextAsync(Path.Combine(directory, "report.json"));
            Assert.Contains("\"Kind\": \"CentralVersion\"", json);
            Assert.Contains("\"VersionSource\": \"Central\"", json);
            Assert.Contains("\"Status\": \"Partial\"", json);
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
