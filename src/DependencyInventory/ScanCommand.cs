using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DependencyInventory;

public sealed class ScanSettings : CommandSettings
{
    [CommandOption("--provider <SOURCE>", isRequired: true)]
    [Description("Source of the code: Azure or GitLab.")]
    public SourceProvider Provider { get; init; }

    [CommandOption("--url")]
    [Description("URL of the Azure DevOps project or the GitLab group.")]
    public string? Url { get; init; }

    [CommandOption("-o|--output")]
    [Description("Folder where the CSV and JSON reports are written.")]
    [DefaultValue("dependency-report")]
    public string Output { get; init; } = "dependency-report";

    [CommandOption("--credentials-file [PATH]")]
    [Description("Credentials read from a file; .azure-credentials or .gitlab-credentials by default. " +
        "Linux and macOS only: on Windows, use the login command.")]
    public required FlagValue<string?> CredentialsFile { get; init; }

    [CommandOption("--pattern <GLOB>")]
    [Description("NuGet package glob (repeatable; * and ?).")]
    public string[] Patterns { get; init; } = [];

    [CommandOption("--regex <EXPRESSION>")]
    [Description(".NET regular expression (repeatable).")]
    public string[] Regexes { get; init; } = [];

    [CommandOption("--case-sensitive")]
    [Description("Make the filters case-sensitive.")]
    public bool CaseSensitive { get; init; }

    [CommandOption("--repository")]
    [Description("Scan a single repository (useful to diagnose missing references).")]
    public string? Repository { get; init; }

    [CommandOption("--diagnostics")]
    [Description("Export the branches and every NuGet reference per file to diagnostics.json.")]
    public bool Diagnostics { get; init; }

    [CommandOption("--parallelism <N>")]
    [Description("Number of simultaneous requests, from 1 (sequential) to 8.")]
    [DefaultValue(1)]
    public int Parallelism { get; init; } = 1;

    [CommandOption("--parallelism-mode <MODE>")]
    [Description("Parallelism mode (files or repositories); required when --parallelism is greater than 1.")]
    public ParallelismMode? ParallelismMode { get; init; }
}

public sealed class ScanCommand : AsyncCommand<ScanSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context, ScanSettings settings, CancellationToken cancellationToken)
    {
        ScanConsole.Header();

        var url = settings.Url ?? AnsiConsole.Ask<string>("[yellow]Source URL[/]:");
        try { RepositoryProviderFactory.ValidateOrigin(settings.Provider, url); }
        catch (ArgumentException ex)
        {
            ScanConsole.Error(ex.Message);
            return 2;
        }

        DependencyMatcher matcher;
        try
        {
            matcher = DependencyMatcher.Create(settings.Patterns, settings.Regexes, settings.CaseSensitive);
        }
        catch (ArgumentException ex)
        {
            ScanConsole.Error($"Invalid filter: {ex.Message}");
            return 2;
        }

        ScanConsole.Filters(matcher);

        ScanParallelism parallelism;
        try { parallelism = ScanParallelism.Create(settings.Parallelism, settings.ParallelismMode); }
        catch (ArgumentException ex)
        {
            ScanConsole.Error(ex.Message);
            return 2;
        }

        // Secrets are never logged; Azure uses login and PAT, GitLab the PAT alone.
        RepositoryCredentials credentials;
        var store = CredentialStores.ForCurrentPlatform();
        try
        {
            var resolved = await CredentialResolver.ResolveAsync(settings.Provider, url,
                settings.CredentialsFile.IsSet, settings.CredentialsFile.Value,
                store, () => CredentialPrompt.Ask(settings.Provider), cancellationToken);
            if (resolved.Credentials is null)
            {
                ScanConsole.Error("Credentials are required.");
                return 2;
            }
            if (resolved.Source == CredentialSource.Store)
                ScanConsole.CredentialsFromStore(store!.Name);
            credentials = resolved.Credentials;
        }
        catch (NotSupportedException ex)
        {
            ScanConsole.Error(ex.Message);
            return 2;
        }
        catch (OperationCanceledException)
        {
            ScanConsole.Warning("Reading interrupted.");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            FormatException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            ScanConsole.Error($"Cannot read the credentials: {ex.Message}");
            return 2;
        }

        try
        {
            using IRepositoryProvider api = RepositoryProviderFactory.Create(
                settings.Provider, url, credentials, ScanConsole.RetryScheduled);
            var repositories = await api.GetRepositoriesAsync(cancellationToken);
            var selectedRepositories = RepositoryScanner.Select(repositories, settings.Repository);
            if (selectedRepositories.Count == 0)
            {
                ScanConsole.Error($"Repository not found: {settings.Repository ?? ""}");
                return 2;
            }

            ScanConsole.Source(api.Origin, repositories.Count, selectedRepositories.Count);
            var result = await ScanConsole.WithProgressAsync(selectedRepositories.Count, progress =>
                RepositoryScanner.ScanAsync(api, selectedRepositories, matcher, settings.Diagnostics,
                    parallelism, progress, cancellationToken));

            var report = new ScanReport(DateTimeOffset.UtcNow, api.Origin, matcher.Filters,
                result.Occurrences, result.Coverage, result.Frameworks);
            var paths = await ReportWriter.SaveAsync(report, settings.Output, cancellationToken);
            if (settings.Diagnostics)
            {
                await ReportWriter.SaveDiagnosticsAsync(result.Diagnostics, paths, cancellationToken);
                ScanConsole.DiagnosticsWritten();
            }
            ScanConsole.Summary(selectedRepositories.Count, result, paths);
            return result.IncompleteRepositories > 0 ? 1 : 0;
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
}
