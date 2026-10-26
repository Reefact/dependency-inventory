using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DependencyInventory;

public sealed class ScanSettings : CommandSettings
{
    [CommandOption("--provider <SOURCE>", isRequired: true)]
    [Description("Source du code : Azure ou GitLab.")]
    public SourceProvider Provider { get; init; }

    [CommandOption("--url")]
    [Description("URL du projet Azure DevOps ou du groupe GitLab.")]
    public string? Url { get; init; }

    [CommandOption("-o|--output")]
    [Description("Dossier où écrire les rapports CSV et JSON.")]
    [DefaultValue("dependency-report")]
    public string Output { get; init; } = "dependency-report";

    [CommandOption("--credentials-file [PATH]")]
    [Description("Identifiants dans un fichier ; par défaut .azure-credentials ou .gitlab-credentials.")]
    public required FlagValue<string?> CredentialsFile { get; init; }

    [CommandOption("--pattern <GLOB>")]
    [Description("Glob de package NuGet (répétable ; * et ?).")]
    public string[] Patterns { get; init; } = [];

    [CommandOption("--regex <EXPRESSION>")]
    [Description("Expression régulière .NET (répétable).")]
    public string[] Regexes { get; init; } = [];

    [CommandOption("--case-sensitive")]
    [Description("Rendre les recherches sensibles à la casse.")]
    public bool CaseSensitive { get; init; }

    [CommandOption("--repository")]
    [Description("N'analyser qu'un dépôt (utile pour diagnostiquer les références manquantes).")]
    public string? Repository { get; init; }

    [CommandOption("--diagnostics")]
    [Description("Exporter les branches et toutes les références NuGet par fichier dans diagnostics.json.")]
    public bool Diagnostics { get; init; }
}

public sealed class ScanCommand : AsyncCommand<ScanSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context, ScanSettings settings, CancellationToken cancellationToken)
    {
        ScanConsole.Header();

        var url = settings.Url ?? AnsiConsole.Ask<string>("[yellow]URL de la source[/] :");
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
            ScanConsole.Error($"Filtre invalide : {ex.Message}");
            return 2;
        }

        ScanConsole.Filters(matcher);

        // On ne journalise jamais les secrets ; Azure utilise login/PAT et GitLab le PAT seul.
        RepositoryCredentials credentials;
        if (settings.CredentialsFile.IsSet)
        {
            try
            {
                var path = CredentialsFile.ResolvePath(settings.Provider, settings.CredentialsFile.Value);
                credentials = await CredentialsFile.ReadAsync(path, settings.Provider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ScanConsole.Warning("Lecture interrompue.");
                return 130;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                FormatException or ArgumentException)
            {
                ScanConsole.Error($"Impossible de lire les identifiants : {ex.Message}");
                return 2;
            }
        }
        else
        {
            var login = settings.Provider == SourceProvider.Azure
                ? AnsiConsole.Ask<string>("[yellow]Login Azure DevOps[/] :").Trim()
                : "";
            var pat = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]PAT de la plateforme[/] :").Secret());
            if ((settings.Provider == SourceProvider.Azure && string.IsNullOrWhiteSpace(login)) ||
                string.IsNullOrWhiteSpace(pat))
            {
                ScanConsole.Error("Identifiants obligatoires.");
                return 2;
            }
            credentials = new RepositoryCredentials(login, pat);
        }

        try
        {
            using IRepositoryProvider api = RepositoryProviderFactory.Create(
                settings.Provider, url, credentials);
            var repositories = await api.GetRepositoriesAsync(cancellationToken);
            var selectedRepositories = RepositoryScanner.Select(repositories, settings.Repository);
            if (selectedRepositories.Count == 0)
            {
                ScanConsole.Error($"Dépôt introuvable : {settings.Repository ?? ""}");
                return 2;
            }

            ScanConsole.Source(api.Origin, repositories.Count, selectedRepositories.Count);
            var result = await ScanConsole.WithProgressAsync(selectedRepositories.Count, progress =>
                RepositoryScanner.ScanAsync(api, selectedRepositories, matcher, settings.Diagnostics,
                    progress, cancellationToken));

            var report = new ScanReport(DateTimeOffset.UtcNow, api.Origin, matcher.Filters,
                result.Occurrences, result.Coverage);
            var paths = await ReportWriter.SaveAsync(report, settings.Output, cancellationToken);
            if (settings.Diagnostics)
            {
                await ReportWriter.SaveDiagnosticsAsync(result.Diagnostics, paths, cancellationToken);
                ScanConsole.DiagnosticsWritten();
            }
            ScanConsole.Summary(selectedRepositories.Count, result, paths);
            return result.IncompleteRepositories > 0 ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            ScanConsole.Warning("Scan interrompu.");
            return 130;
        }
        catch (HttpRequestException ex)
        {
            ScanConsole.Error(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            ScanConsole.Error($"Échec : {ex.Message}");
            return 1;
        }
    }
}
