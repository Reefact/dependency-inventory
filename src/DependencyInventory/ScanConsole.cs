using Spectre.Console;

namespace DependencyInventory;

/// <summary>Console rendering of a scan: the only place, with the prompts of ScanCommand, that uses AnsiConsole.</summary>
public static class ScanConsole
{
    public static void Header()
    {
        AnsiConsole.Write(new Rule("[cyan]Dependency Inventory[/]").LeftJustified());
        AnsiConsole.MarkupLine("Scan en lecture seule ; [bold]une branche[/] par dépôt.");
    }

    public static void Filters(DependencyMatcher matcher) =>
        AnsiConsole.MarkupLine("Filtres (OU logique) : " +
            Markup.Escape(string.Join(", ", matcher.Filters)));

    public static void Source(string origin, int accessible, int selected) =>
        AnsiConsole.MarkupLine($"Source : [blue]{Markup.Escape(origin)}[/] — " +
            $"{accessible} dépôt(s) accessibles, {selected} à analyser.");

    public static void Error(string message) =>
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");

    public static void Warning(string message) =>
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");

    public static void RetryScheduled(RetryNotice notice) =>
        AnsiConsole.MarkupLine($"[yellow]Limite de requêtes atteinte (HTTP {(int)notice.Status}), " +
            $"reprise dans {Math.Ceiling(notice.Delay.TotalSeconds):0} s " +
            $"(tentative {notice.Attempt + 1}/{notice.MaxAttempts}).[/]");

    public static void DiagnosticsWritten() =>
        AnsiConsole.MarkupLine("[blue]Diagnostic complet : diagnostics.json[/]");

    /// <summary>Runs the scan behind a progress bar with one step per repository.</summary>
    public static async Task<T> WithProgressAsync<T>(int repositories, Func<IScanProgress, Task<T>> scan)
    {
        T result = default!;
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Inspection", maxValue: Math.Max(repositories, 1));
                result = await scan(new ProgressBar(task));
            });
        return result;
    }

    public static void Summary(int analysed, ScanResult result, string reportDirectory)
    {
        var references = result.Occurrences.Where(x => x.Kind == PackageKind.Reference).ToArray();
        var consumers = references.Select(x => x.Repository).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var summary = new Table().Border(TableBorder.Rounded)
            .AddColumn("Mesure").AddColumn("Valeur")
            .AddRow("Dépôts analysés", analysed.ToString())
            .AddRow("Dépôts consommateurs", consumers.ToString())
            .AddRow("Références détectées", references.Length.ToString())
            .AddRow("Packages distincts", references.Select(x => x.Package)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString())
            .AddRow("Dépôts en erreur ou partiels", result.IncompleteRepositories.ToString());
        AnsiConsole.Write(summary);
        AnsiConsole.MarkupLine($"Rapports : [green]{Markup.Escape(reportDirectory)}[/]");
        if (result.IncompleteRepositories > 0)
            AnsiConsole.MarkupLine("[yellow]Inspection incomplète : consulter repositories.csv.[/]");
    }

    private sealed class ProgressBar(ProgressTask task) : IScanProgress
    {
        public void RepositoryStarted(Repository repository) =>
            task.Description = Markup.Escape(repository.Name);

        public void RepositoryCompleted(Repository repository) => task.Increment(1);
    }
}
