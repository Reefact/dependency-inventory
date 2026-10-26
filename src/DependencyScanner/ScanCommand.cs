using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DependencyScanner;

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
        AnsiConsole.Write(new Rule("[cyan]Dependency Scanner[/]").LeftJustified());
        AnsiConsole.MarkupLine("Scan en lecture seule ; [bold]une branche[/] par dépôt.");

        var url = settings.Url ?? AnsiConsole.Ask<string>("[yellow]URL de la source[/] :");
        try { RepositoryProviderFactory.ValidateOrigin(settings.Provider, url); }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 2;
        }

        DependencyMatcher matcher;
        try
        {
            matcher = DependencyMatcher.Create(settings.Patterns, settings.Regexes, settings.CaseSensitive);
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]Filtre invalide : {Markup.Escape(ex.Message)}[/]");
            return 2;
        }

        AnsiConsole.MarkupLine("Filtres (OU logique) : " +
            Markup.Escape(string.Join(", ", matcher.Filters)));

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
                AnsiConsole.MarkupLine("[yellow]Lecture interrompue.[/]");
                return 130;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                FormatException or ArgumentException)
            {
                AnsiConsole.MarkupLine($"[red]Impossible de lire les identifiants : {Markup.Escape(ex.Message)}[/]");
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
                AnsiConsole.MarkupLine("[red]Identifiants obligatoires.[/]");
                return 2;
            }
            credentials = new RepositoryCredentials(login, pat);
        }

        try
        {
            using IRepositoryProvider api = RepositoryProviderFactory.Create(
                settings.Provider, url, credentials);
            var repositories = await api.GetRepositoriesAsync(cancellationToken);
            var occurrences = new List<PackageOccurrence>();
            var coverage = new List<RepositoryCoverage>();
            var diagnostics = new List<RepositoryDiagnostic>();
            var selectedRepositories = repositories
                .Where(r => settings.Repository is null ||
                    r.Name.Equals(settings.Repository, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (selectedRepositories.Length == 0)
            {
                AnsiConsole.MarkupLine($"[red]Dépôt introuvable : {Markup.Escape(settings.Repository ?? "")}[/]");
                return 2;
            }

            AnsiConsole.MarkupLine($"Source : [blue]{Markup.Escape(api.Origin)}[/] — " +
                $"{repositories.Count} dépôt(s) accessibles, {selectedRepositories.Length} à analyser.");

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                .StartAsync(async ctx =>
                {
                    var progress = ctx.AddTask("Inspection", maxValue: Math.Max(selectedRepositories.Length, 1));
                    foreach (var repo in selectedRepositories.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress.Description = Markup.Escape(repo.Name);
                        var diagnostic = settings.Diagnostics
                            ? new RepositoryDiagnostic { Project = repo.Project, Repository = repo.Name }
                            : null;
                        var result = await ScanRepositoryAsync(api, repo, matcher, diagnostic, cancellationToken);
                        occurrences.AddRange(result.Hits);
                        coverage.Add(result.Coverage);
                        if (diagnostic is not null) diagnostics.Add(diagnostic);
                        progress.Increment(1);
                    }
                });

            var report = new ScanReport(DateTimeOffset.UtcNow, api.Origin, matcher.Filters, occurrences, coverage);
            var paths = await ReportWriter.SaveAsync(report, settings.Output, cancellationToken);
            if (settings.Diagnostics)
            {
                var diagnosticJson = JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                await File.WriteAllTextAsync(Path.Combine(paths, "diagnostics.json"),
                    diagnosticJson, cancellationToken);
                AnsiConsole.MarkupLine("[blue]Diagnostic complet : diagnostics.json[/]");
            }
            var references = occurrences.Where(x => x.Kind == PackageKind.Reference).ToArray();
            var consumers = references.Select(x => x.Repository).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var issues = coverage.Count(x => x.Status is CoverageStatus.Partial or CoverageStatus.Failed);

            var summary = new Table().Border(TableBorder.Rounded)
                .AddColumn("Mesure").AddColumn("Valeur")
                .AddRow("Dépôts analysés", selectedRepositories.Length.ToString())
                .AddRow("Dépôts consommateurs", consumers.ToString())
                .AddRow("Références détectées", references.Length.ToString())
                .AddRow("Packages distincts", references.Select(x => x.Package)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString())
                .AddRow("Dépôts en erreur ou partiels", issues.ToString());
            AnsiConsole.Write(summary);
            AnsiConsole.MarkupLine($"Rapports : [green]{Markup.Escape(paths)}[/]");
            if (issues > 0)
                AnsiConsole.MarkupLine("[yellow]Inspection incomplète : consulter repositories.csv.[/]");
            return issues > 0 ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Scan interrompu.[/]");
            return 130;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Échec : {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static async Task<(RepositoryCoverage Coverage, List<PackageOccurrence> Hits)>
        ScanRepositoryAsync(IRepositoryProvider api, Repository repo,
            DependencyMatcher matcher, RepositoryDiagnostic? diagnostic, CancellationToken ct)
    {
        var hits = new List<PackageOccurrence>();
        if (repo.IsDisabled)
            return (new(repo.Project, repo.Name, null, CoverageStatus.Disabled, 0, 0, "Dépôt désactivé"), hits);

        string? branchName = null;
        int filesRead = 0;
        var failures = new List<string>();
        try
        {
            var branches = await api.GetBranchesAsync(repo.Id, ct);
            if (diagnostic is not null)
                diagnostic.AvailableBranches.AddRange(branches.Select(b => b.Name));
            var branch = BranchSelector.Select(branches);
            if (branch is null)
                return (new(repo.Project, repo.Name, null, CoverageStatus.NoBranch, 0, 0, "Aucune branche Git"), hits);
            branchName = branch.Name;
            if (diagnostic is not null)
            {
                diagnostic.SelectedBranch = branch.Name;
                diagnostic.Commit = branch.ObjectId;
            }
            var tree = await api.GetTreeAsync(repo.Id, branch.ObjectId, ct);
            var paths = tree.Where(x => !x.IsFolder && NuGetDependencyParser.IsInterestingFile(x.Path))
                .Select(x => x.Path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

            // Toutes les références proviennent EXCLUSIVEMENT de ce commit.
            var parsedFiles = new Dictionary<string, IReadOnlyList<ParsedPackage>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var content = await api.GetFileAsync(repo.Id, branch.ObjectId, path, ct);
                    var packages = NuGetDependencyParser.Parse(path, content);
                    parsedFiles[path] = packages;
                    diagnostic?.Files.Add(new FileDiagnostic(path, content.Length, packages, null));
                    filesRead++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failures.Add($"{path}: {ex.Message}");
                    diagnostic?.Files.Add(new FileDiagnostic(path, null, [], ex.Message));
                }
            }

            foreach (var file in parsedFiles)
            foreach (var package in file.Value)
            {
                var matchedFilters = matcher.Match(package.Id);
                if (matchedFilters.Count == 0) continue;

                var version = package.Version;
                var source = package.VersionSource;
                if (package.Kind == PackageKind.Reference && version is null &&
                    (file.Key.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                     file.Key.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                     file.Key.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)))
                {
                    version = NuGetDependencyParser.ResolveCentralVersion(file.Key, package.Id, parsedFiles);
                    if (version is not null) source = VersionSource.Central;
                }
                hits.Add(new PackageOccurrence(repo.Project, repo.Name, branch.Name,
                    branch.ObjectId, file.Key, package.Id, version, package.Kind, source,
                    matchedFilters, api.FileUrl(repo, branch.ObjectId, file.Key)));
            }
            var status = failures.Count > 0 ? CoverageStatus.Partial
                : paths.Length == 0 ? CoverageStatus.NoNuGetFiles : CoverageStatus.OK;
            return (new(repo.Project, repo.Name, branchName, status,
                filesRead, hits.Count(x => x.Kind == PackageKind.Reference),
                failures.Count > 0 ? string.Join(" | ", failures.Take(5)) : null), hits);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (new(repo.Project, repo.Name, branchName, CoverageStatus.Failed, filesRead, 0, ex.Message), hits);
        }
    }

    private sealed class RepositoryDiagnostic
    {
        public string Project { get; init; } = "";
        public string Repository { get; init; } = "";
        public string? SelectedBranch { get; set; }
        public string? Commit { get; set; }
        public List<string> AvailableBranches { get; } = [];
        public List<FileDiagnostic> Files { get; } = [];
    }

    private sealed record FileDiagnostic(
        string Path,
        int? ContentLength,
        IReadOnlyList<ParsedPackage> Packages,
        string? Error);
}
