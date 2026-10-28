using System.ComponentModel;
using Spectre.Console.Cli;

namespace DependencyInventory;

public sealed class PackagesSettings : RepositorySettings
{
    [CommandOption("-o|--output")]
    [Description("Folder where the CSV and JSON reports are written.")]
    [DefaultValue("dependency-report")]
    public string Output { get; init; } = "dependency-report";

    [CommandOption("--pattern <GLOB>")]
    [Description("NuGet package glob (repeatable; * and ?).")]
    public string[] Patterns { get; init; } = [];

    [CommandOption("--regex <EXPRESSION>")]
    [Description(".NET regular expression (repeatable).")]
    public string[] Regexes { get; init; } = [];

    [CommandOption("--case-sensitive")]
    [Description("Make the filters case-sensitive.")]
    public bool CaseSensitive { get; init; }

    [CommandOption("--feed <FEED>")]
    [Description("Azure Artifacts feed the referenced packages are read from, to tell whether the code uses them " +
        "(repeatable, in priority order): name or project/name of a feed of the --url organization, or the feed URL.")]
    public string[] Feeds { get; init; } = [];

    [CommandOption("--no-nuget-org")]
    [Description("Do not read the referenced packages from nuget.org, otherwise read after the feeds.")]
    public bool NoNuGetOrg { get; init; }

    [CommandOption("--used-types")]
    [Description("Also list the types of each package referenced by the code, in used-types.csv.")]
    public bool UsedTypes { get; init; }
}

/// <summary>depinv packages: the NuGet packages matching the filters, in every repository.</summary>
public sealed class PackagesCommand : RepositoryCommand<PackagesSettings>
{
    private DependencyMatcher? _matcher;
    private IReadOnlyList<AzureFeed> _feeds = [];
    private readonly Dictionary<string, RepositoryCredentials> _feedCredentials = new(StringComparer.OrdinalIgnoreCase);

    protected override bool Prepare(PackagesSettings settings, string url)
    {
        try
        {
            _matcher = DependencyMatcher.Create(settings.Patterns, settings.Regexes, settings.CaseSensitive);
        }
        catch (ArgumentException ex)
        {
            ScanConsole.Error($"Invalid filter: {ex.Message}");
            return false;
        }
        try
        {
            var origin = settings.Provider == SourceProvider.Azure ? AzureOrigin.Parse(url) : null;
            _feeds = settings.Feeds.Select(f => AzureFeed.Parse(origin, f)).Distinct().ToArray();
        }
        catch (ArgumentException ex)
        {
            ScanConsole.Error(ex.Message);
            return false;
        }
        if (settings.NoNuGetOrg && _feeds.Count == 0)
        {
            ScanConsole.Error("--no-nuget-org leaves no package source to tell whether the code uses the packages: " +
                "add --feed.");
            return false;
        }
        ScanConsole.Filters(_matcher);
        ScanConsole.PackageSources([.. _feeds.Select(f => $"feed {f.DisplayName}"),
            .. settings.NoNuGetOrg ? Array.Empty<string>() : ["nuget.org"]]);
        return true;
    }

    /// <summary>
    /// The feeds of the origin's organization are read with its credentials; those of another organization, with
    /// that organization's.
    /// </summary>
    protected override async Task<int> AuthenticateAsync(PackagesSettings settings, string url,
        RepositoryCredentials credentials, CancellationToken cancellationToken)
    {
        if (settings.Provider == SourceProvider.Azure)
            _feedCredentials[AzureOrigin.Parse(url).Organization] = credentials;
        foreach (var organization in _feeds.Select(f => f.Organization).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_feedCredentials.ContainsKey(organization)) continue;
            var (resolved, exitCode) =
                await OriginCredentials.ResolveOrganizationAsync(settings, organization, cancellationToken);
            if (resolved is null) return exitCode;
            _feedCredentials[organization] = resolved;
        }
        return 0;
    }

    protected override async Task<int> InventoryAsync(IRepositoryProvider api,
        IReadOnlyList<Repository> repositories, ScanParallelism parallelism, PackagesSettings settings,
        CancellationToken cancellationToken)
    {
        var matcher = _matcher!;
        var sources = new List<PackageSourceEntry>();
        try
        {
            foreach (var feed in _feeds)
            {
                var credentials = _feedCredentials[feed.Organization];
                sources.Add(new PackageSourceEntry(new AzureArtifactsFeed(feed, credentials.Login, credentials.Pat,
                    ScanConsole.RetryScheduled), PackageSource.Feed, feed.DisplayName));
            }
            if (!settings.NoNuGetOrg)
                sources.Add(new PackageSourceEntry(new NuGetOrgFeed(ScanConsole.RetryScheduled),
                    PackageSource.NuGetOrg, null));
            var catalog = new PackageCatalog(sources, PackageCatalog.DefaultCacheDirectory(),
                PackageCatalog.DefaultGlobalPackagesFolder());
            var usage = new PackageUsageAnalyzer(catalog, settings.UsedTypes);

            var result = await ScanConsole.WithProgressAsync(repositories.Count, progress =>
                RepositoryScanner.ScanAsync(api, repositories, matcher, settings.Diagnostics,
                    parallelism, progress, cancellationToken, usage));
            foreach (var failure in catalog.SourceFailures)
                ScanConsole.Warning($"Package source failure: {failure}");
            return await WriteReportsAsync(api, repositories, settings, matcher, result, cancellationToken);
        }
        finally
        {
            foreach (var source in sources) source.Dispose();
        }
    }

    private static async Task<int> WriteReportsAsync(IRepositoryProvider api, IReadOnlyList<Repository> repositories,
        PackagesSettings settings, DependencyMatcher matcher, ScanResult result, CancellationToken cancellationToken)
    {
        var report = new ScanReport(DateTimeOffset.UtcNow, api.Origin, matcher.Filters,
            result.Occurrences, result.Coverage);
        var paths = await ReportWriter.SaveAsync(report, settings.Output, settings.UsedTypes, cancellationToken);
        if (settings.Diagnostics)
        {
            await ReportWriter.SaveDiagnosticsAsync(result.Diagnostics, paths, cancellationToken);
            ScanConsole.DiagnosticsWritten();
        }
        ScanConsole.Summary(repositories.Count, result, paths);
        return result.IncompleteRepositories > 0 ? 1 : 0;
    }
}
