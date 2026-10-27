using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DependencyInventory;

public sealed class FeedSettings : OriginSettings
{
    [CommandOption("--feed <FEED>")]
    [Description("Azure Artifacts feed: its name for an organization feed, project/name for a project feed.")]
    public string? Feed { get; init; }

    [CommandOption("--package <ID[@VERSION]>")]
    [Description("Package to read (repeatable): ID@VERSION for one version, ID for every published version.")]
    public string[] Packages { get; init; } = [];

    [CommandOption("--versions-from <CSV>")]
    [Description("Read the package versions listed in a dependencies.csv written by depinv packages.")]
    public string? VersionsFrom { get; init; }

    [CommandOption("--pattern <GLOB>")]
    [Description("Read every published version of the feed's packages matching this glob (repeatable; * and ?).")]
    public string[] Patterns { get; init; } = [];

    [CommandOption("--regex <EXPRESSION>")]
    [Description("Read every published version of the feed's packages matching this .NET regular expression (repeatable).")]
    public string[] Regexes { get; init; } = [];

    [CommandOption("--latest")]
    [Description("Only the highest version, prereleases included, of the packages read without a version.")]
    public bool Latest { get; init; }

    [CommandOption("--dependency-pattern <GLOB>")]
    [Description("Only report the dependencies matching this glob (repeatable; * and ?). All of them by default.")]
    public string[] DependencyPatterns { get; init; } = [];

    [CommandOption("--dependency-regex <EXPRESSION>")]
    [Description("Only report the dependencies matching this .NET regular expression (repeatable).")]
    public string[] DependencyRegexes { get; init; } = [];

    [CommandOption("--case-sensitive")]
    [Description("Make the package and dependency filters case-sensitive.")]
    public bool CaseSensitive { get; init; }

    [CommandOption("-o|--output")]
    [Description("Folder where the CSV and JSON reports are written.")]
    [DefaultValue("feed-report")]
    public string Output { get; init; } = "feed-report";

    [CommandOption("--diagnostics")]
    [Description("Export the frameworks and every dependency read for each version to diagnostics.json.")]
    public bool Diagnostics { get; init; }

    [CommandOption("--parallelism <N>")]
    [Description("Number of package versions read at once, from 1 (sequential) to 8.")]
    [DefaultValue(1)]
    public int Parallelism { get; init; } = 1;
}

/// <summary>
/// depinv feed: the frameworks and dependencies of published package versions, read from an Azure Artifacts feed.
/// Exit codes: 0 complete, 1 a package or version not found or not read, or the feed failed, 2 invalid input,
/// 130 interrupted.
/// </summary>
public sealed class FeedCommand : AsyncCommand<FeedSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context, FeedSettings settings, CancellationToken cancellationToken)
    {
        ScanConsole.FeedHeader();
        if (settings.Provider != SourceProvider.Azure)
        {
            ScanConsole.Error("depinv feed reads Azure Artifacts feeds only for now: use --provider azure.");
            return 2;
        }

        var url = settings.Url ?? AnsiConsole.Ask<string>("[yellow]Source URL[/]:");
        AzureFeed feedName;
        try { feedName = AzureFeed.Parse(AzureOrigin.Parse(url), settings.Feed); }
        catch (ArgumentException ex)
        {
            ScanConsole.Error(ex.Message);
            return 2;
        }

        var packages = new List<PackageRequest>();
        DependencyMatcher? packageFilter, dependencyFilter;
        try
        {
            packages.AddRange(settings.Packages.Select(PackageRequest.Parse));
            packageFilter = OptionalMatcher(settings.Patterns, settings.Regexes, settings.CaseSensitive);
            dependencyFilter = OptionalMatcher(settings.DependencyPatterns, settings.DependencyRegexes,
                settings.CaseSensitive);
            if (settings.Parallelism is < 1 or > ScanParallelism.MaxDegree)
                throw new ArgumentException($"--parallelism must be between 1 and {ScanParallelism.MaxDegree}.");
        }
        catch (ArgumentException ex)
        {
            ScanConsole.Error(ex.Message);
            return 2;
        }

        if (settings.VersionsFrom is { } file)
        {
            try
            {
                var (requests, skipped) = await VersionsFile.ReadAsync(file, cancellationToken);
                packages.AddRange(requests);
                if (skipped > 0)
                    ScanConsole.Warning($"{skipped} lines of {file} have no literal version and are skipped.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                ScanConsole.Error($"Cannot read {file}: {ex.Message}");
                return 2;
            }
        }

        if (packages.Count == 0 && packageFilter is null)
        {
            ScanConsole.Error("Specify the packages to read: --package, --versions-from, --pattern or --regex.");
            return 2;
        }
        if (packageFilter is not null) ScanConsole.Filters(packageFilter);

        var (credentials, exitCode) = await OriginCredentials.ResolveAsync(settings, url, cancellationToken);
        if (credentials is null) return exitCode;

        try
        {
            using IPackageFeed feed = new AzureArtifactsFeed(feedName, credentials.Login, credentials.Pat,
                ScanConsole.RetryScheduled);
            ScanConsole.Resolving();
            var resolution = await FeedScanner.ResolveAsync(feed,
                new FeedRequest(packages, packageFilter, settings.Latest), cancellationToken);
            ScanConsole.Feed(feed.Origin, resolution.Versions.Count, resolution.Unknown.Count);

            var result = await ScanConsole.WithFeedProgressAsync(resolution.Versions.Count, progress =>
                FeedScanner.ReadAsync(feed, resolution, dependencyFilter, settings.Parallelism, progress,
                    cancellationToken));

            var report = new FeedReport(DateTimeOffset.UtcNow, feed.Origin, packageFilter?.Filters ?? [],
                dependencyFilter?.Filters ?? [], result.Frameworks, result.Dependencies, result.Packages);
            var paths = await ReportWriter.SaveFeedAsync(report, settings.Output, cancellationToken);
            if (settings.Diagnostics)
            {
                await ReportWriter.SaveDiagnosticsAsync(result.Diagnostics, paths, cancellationToken);
                ScanConsole.DiagnosticsWritten();
            }
            ScanConsole.FeedSummary(result, paths);
            return result.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ScanConsole.Warning("Scan interrupted.");
            return 130;
        }
        catch (HttpRequestException ex)
        {
            ScanConsole.Error(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            ScanConsole.Error($"Failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>No filter when no glob nor expression is given.</summary>
    private static DependencyMatcher? OptionalMatcher(string[] globs, string[] expressions, bool caseSensitive) =>
        globs.Length + expressions.Length == 0 ? null
            : DependencyMatcher.Create(globs, expressions, caseSensitive);
}
