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
}

/// <summary>depinv packages: the NuGet packages matching the filters, in every repository.</summary>
public sealed class PackagesCommand : RepositoryCommand<PackagesSettings>
{
    private DependencyMatcher? _matcher;

    protected override bool Prepare(PackagesSettings settings)
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
        ScanConsole.Filters(_matcher);
        return true;
    }

    protected override async Task<int> InventoryAsync(IRepositoryProvider api,
        IReadOnlyList<Repository> repositories, ScanParallelism parallelism, PackagesSettings settings,
        CancellationToken cancellationToken)
    {
        var matcher = _matcher!;
        var result = await ScanConsole.WithProgressAsync(repositories.Count, progress =>
            RepositoryScanner.ScanAsync(api, repositories, matcher, settings.Diagnostics,
                parallelism, progress, cancellationToken));

        var report = new ScanReport(DateTimeOffset.UtcNow, api.Origin, matcher.Filters,
            result.Occurrences, result.Coverage);
        var paths = await ReportWriter.SaveAsync(report, settings.Output, cancellationToken);
        if (settings.Diagnostics)
        {
            await ReportWriter.SaveDiagnosticsAsync(result.Diagnostics, paths, cancellationToken);
            ScanConsole.DiagnosticsWritten();
        }
        ScanConsole.Summary(repositories.Count, result, paths);
        return result.IncompleteRepositories > 0 ? 1 : 0;
    }
}
