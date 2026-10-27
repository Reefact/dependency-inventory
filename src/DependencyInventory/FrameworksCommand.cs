using System.ComponentModel;
using Spectre.Console.Cli;

namespace DependencyInventory;

public sealed class FrameworksSettings : RepositorySettings
{
    [CommandOption("-o|--output")]
    [Description("Folder where the CSV and JSON reports are written.")]
    [DefaultValue("framework-report")]
    public string Output { get; init; } = "framework-report";
}

/// <summary>depinv frameworks: the target frameworks of every .NET project, in every repository.</summary>
public sealed class FrameworksCommand : RepositoryCommand<FrameworksSettings>
{
    protected override async Task<int> InventoryAsync(IRepositoryProvider api,
        IReadOnlyList<Repository> repositories, ScanParallelism parallelism, FrameworksSettings settings,
        CancellationToken cancellationToken)
    {
        var result = await ScanConsole.WithProgressAsync(repositories.Count, progress =>
            FrameworkScanner.ScanAsync(api, repositories, settings.Diagnostics,
                parallelism, progress, cancellationToken));

        var report = new FrameworkReport(DateTimeOffset.UtcNow, api.Origin, result.Frameworks, result.Coverage);
        var paths = await ReportWriter.SaveFrameworksAsync(report, settings.Output, cancellationToken);
        if (settings.Diagnostics)
        {
            await ReportWriter.SaveDiagnosticsAsync(result.Diagnostics, paths, cancellationToken);
            ScanConsole.DiagnosticsWritten();
        }
        ScanConsole.FrameworkSummary(repositories.Count, result, paths);
        return result.IncompleteRepositories > 0 ? 1 : 0;
    }
}
