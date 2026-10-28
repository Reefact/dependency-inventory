using Xunit;

namespace DependencyInventory.Tests;

public sealed class BranchSelectionTests
{
    private static string? Select(string names, string[]? requested, string? defaultBranch = null) =>
        BranchSelector.Select(names.Split(',').Select(name => new GitBranch(name, "aaaa")), requested,
            defaultBranch)?.Name;

    [Fact]
    public void TheFirstRequestedBranchThatExistsIsSelected()
    {
        Assert.Equal("main", Select("develop,main,release", ["master", "main", "release"]));
        Assert.Equal("release", Select("develop,release", ["main", "release", "develop"]));
    }

    [Fact]
    public void NamesAreComparedCaseInsensitively() =>
        Assert.Equal("Release", Select("develop,Release", ["release"]));

    [Fact]
    public void AGlobTakesTheHighestMatchingBranchInNaturalOrder()
    {
        Assert.Equal("release/1.10", Select("release/1.9,release/1.10,release/1.2,develop", ["release/*"]));
        Assert.Equal("feature/a/b", Select("main,feature/a/b", ["feature/*"]));
    }

    [Fact]
    public void WithoutAListTheDefaultBranchIsSelected()
    {
        Assert.Equal("develop", Select("main,develop,release", null, "develop"));
        Assert.Null(Select("main,develop", null));
    }

    [Fact]
    public void DefaultStandsForTheDefaultBranch()
    {
        Assert.Equal("trunk", Select("trunk,feature/x", ["@default"], "trunk"));
        Assert.Equal("release", Select("trunk,release", ["release", "@default"], "trunk"));
        Assert.Equal("trunk", Select("trunk,develop", ["release", "@default"], "trunk"));
    }

    [Fact]
    public void NoRequestedBranchMeansNone() =>
        Assert.Null(Select("develop,feature/x", ["release", "main"], "develop"));

    [Fact]
    public void ABranchWithoutCommitIsIgnored() =>
        Assert.Equal("main", BranchSelector.Select(
            [new GitBranch("release", "0000000000"), new GitBranch("main", "abcd")], ["release", "main"], null)?.Name);

    [Fact]
    public void TheBranchFileHasADefaultListAndRepositorySections()
    {
        var policy = BranchPolicy.Parse("""
            # Default list
            release

            master
            @default

            [core-*]
            master
            [Core-Logging]
            develop
            """, "branches.txt");

        Assert.Equal("release, master, @default | -", Normalize(policy.For("api-deposit")));
        Assert.Equal("master | core-*", Normalize(policy.For("core-logging")));
        Assert.Equal("master | core-*", Normalize(policy.For("CORE-cache")));
    }

    [Fact]
    public void WithSectionsOnlyOtherRepositoriesUseTheirDefaultBranch()
    {
        var policy = BranchPolicy.Parse("[core-*]\nmaster\n", "branches.txt");

        Assert.Equal("default branch | -", Normalize(policy.For("api-deposit")));
        Assert.Equal("master | core-*", Normalize(policy.For("core-cache")));
    }

    [Theory]
    [InlineData("release\n[core-*]\n\n# nothing\n[api]\nmain\n", "branches.txt, line 2: the section [core-*] lists no branch.")]
    [InlineData("release\n[core-*\nmaster\n", "branches.txt, line 2: a section is written [repository], a name or a glob.")]
    [InlineData("[ ]\nmaster\n", "branches.txt, line 1: a section is written [repository], a name or a glob.")]
    [InlineData("# nothing yet\n\n", "branches.txt: the file lists no branch.")]
    public void AMalformedBranchFileNamesTheLine(string text, string message) =>
        Assert.Equal(message, Assert.Throws<FormatException>(() => BranchPolicy.Parse(text, "branches.txt")).Message);

    [Fact]
    public void TheOptionsGiveTheListTheFileOrTheDefaultBranch()
    {
        var file = Path.Combine(Path.GetTempPath(), $"depinv-branches-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "release\nmain\n");
        try
        {
            Assert.Equal("main | -", Normalize(BranchPolicy.FromOptions(["main"], null).For("api")));
            Assert.Equal("release, main | -", Normalize(BranchPolicy.FromOptions(null, file).For("api")));
            Assert.Equal("default branch | -", Normalize(BranchPolicy.FromOptions(null, null).For("api")));
            Assert.Equal($"release, main (the first that exists), from {file}",
                BranchPolicy.FromOptions([], file).Describe());
            Assert.Equal("the default branch of each repository", BranchPolicy.FromOptions(null, null).Describe());

            Assert.Equal("Use --branch or --branch-file, not both.",
                Assert.Throws<ArgumentException>(() => BranchPolicy.FromOptions(["main"], file)).Message);
            Assert.StartsWith("Branch file not found:",
                Assert.Throws<ArgumentException>(() => BranchPolicy.FromOptions(null, file + ".missing")).Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task TheScanUsesTheListThatAppliesToEachRepository()
    {
        var api = new FakeRepositoryProvider()
            .AddRepository("api", branch: "develop", otherBranches: ["release"])
            .AddRepository("core-cache", branch: "develop", otherBranches: ["master", "release"])
            .AddRepository("tool", branch: "trunk", otherBranches: ["feature/x"])
            .AddRepository("lib", branch: "feature/y", noDefaultBranch: true);
        var policy = BranchPolicy.Parse("release\n@default\n[core-*]\nmaster\n[lib]\nmain\n", "branches.txt");

        var result = await RepositoryScanner.ScanAsync(api, api.Repositories,
            DependencyMatcher.Create(["*"], null), false, ScanParallelism.Create(1, null), null,
            CancellationToken.None, branches: policy);

        Assert.Equal(["api release NoNuGetFiles", "core-cache master NoNuGetFiles", "tool trunk NoNuGetFiles", "lib  NoBranch"],
            result.Coverage.Select(c => $"{c.Repository} {c.Branch} {c.Status}"));
        Assert.Equal("None of the requested branches exists: main (section [lib])", result.Coverage[3].Details);
    }

    [Fact]
    public async Task WithoutBranchOptionsTheFrameworksAreReadOnTheDefaultBranch()
    {
        var api = new FakeRepositoryProvider()
            .AddRepository("api", branch: "develop", otherBranches: ["main"])
            .AddRepository("empty", branch: "main", noDefaultBranch: true);

        var result = await FrameworkScanner.ScanAsync(api, api.Repositories, false, ScanParallelism.Create(1, null),
            null, CancellationToken.None);

        Assert.Equal(["api develop", "empty "], result.Coverage.Select(c => $"{c.Repository} {c.Branch}"));
        Assert.Equal("No default branch", result.Coverage[1].Details);
    }

    // The list and its section as text: tuples of arrays compare by reference.
    private static string Normalize((IReadOnlyList<string>? List, string? Section) value) =>
        $"{(value.List is null ? "default branch" : string.Join(", ", value.List))} | {value.Section ?? "-"}";
}

public sealed class DefaultBranchTests
{
    [Theory]
    [InlineData("""{"defaultBranch": "refs/heads/release"}""", "release")]
    [InlineData("""{"defaultBranch": "refs/heads/feature/x"}""", "feature/x")]
    [InlineData("""{"name": "empty"}""", null)]
    [InlineData("""{"defaultBranch": null}""", null)]
    public void AzureDevOpsGivesTheDefaultBranchOfARepository(string json, string? expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(expected, AzureDevOpsClient.DefaultBranch(document.RootElement));
    }

    [Theory]
    [InlineData("""{"default_branch": "main"}""", "main")]
    [InlineData("""{"default_branch": null}""", null)]
    [InlineData("""{"name": "empty"}""", null)]
    public void GitLabGivesTheDefaultBranchOfAProject(string json, string? expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(expected, GitLabRepositoryProvider.DefaultBranch(document.RootElement));
    }
}
