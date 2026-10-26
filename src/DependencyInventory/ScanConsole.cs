using System.Net;
using Spectre.Console;

namespace DependencyInventory;

/// <summary>Console rendering of a scan: the only place, with the prompts of ScanCommand, that uses AnsiConsole.</summary>
public static class ScanConsole
{
    public static void Header()
    {
        AnsiConsole.Write(new Rule("[cyan]Dependency Inventory[/]").LeftJustified());
        AnsiConsole.MarkupLine("Read-only scan; [bold]one branch[/] per repository.");
    }

    public static void Filters(DependencyMatcher matcher) =>
        AnsiConsole.MarkupLine("Filters (logical OR): " +
            Markup.Escape(string.Join(", ", matcher.Filters)));

    public static void Source(string origin, int accessible, int selected) =>
        AnsiConsole.MarkupLine($"Source: [blue]{Markup.Escape(origin)}[/] — " +
            $"{accessible} accessible repositories, {selected} to scan.");

    public static void CredentialsFromStore(string store) =>
        AnsiConsole.MarkupLine($"Credentials: {Markup.Escape(store)}.");

    public static void Error(string message) =>
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");

    public static void Warning(string message) =>
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");

    public static void RetryScheduled(RetryNotice notice)
    {
        var cause = notice switch
        {
            { Status: HttpStatusCode.TooManyRequests } => "Rate limit reached (HTTP 429)",
            { Status: { } status } => $"Service unavailable (HTTP {(int)status})",
            { Error: TaskCanceledException } => "Response timed out",
            _ => "Network error"
        };
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(cause)}, " +
            $"retrying in {Math.Ceiling(notice.Delay.TotalSeconds):0} s " +
            $"(attempt {notice.Attempt + 1}/{notice.MaxAttempts}).[/]");
    }

    public static void DiagnosticsWritten() =>
        AnsiConsole.MarkupLine("[blue]Full diagnostics: diagnostics.json[/]");

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
            .AddColumn("Measure").AddColumn("Value")
            .AddRow("Repositories scanned", analysed.ToString())
            .AddRow("Consuming repositories", consumers.ToString())
            .AddRow("References found", references.Length.ToString())
            .AddRow("Distinct packages", references.Select(x => x.Package)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString())
            .AddRow("Failed or partial repositories", result.IncompleteRepositories.ToString());
        AnsiConsole.Write(summary);
        AnsiConsole.MarkupLine($"Reports: [green]{Markup.Escape(reportDirectory)}[/]");
        if (result.IncompleteRepositories > 0)
            AnsiConsole.MarkupLine("[yellow]Incomplete scan: see repositories.csv.[/]");
    }

    // Notified concurrently when repositories are scanned in parallel.
    private sealed class ProgressBar(ProgressTask task) : IScanProgress
    {
        private readonly Lock _lock = new();

        public void RepositoryStarted(Repository repository)
        {
            lock (_lock) task.Description = Markup.Escape(repository.Name);
        }

        public void RepositoryCompleted(Repository repository)
        {
            lock (_lock) task.Increment(1);
        }
    }
}
