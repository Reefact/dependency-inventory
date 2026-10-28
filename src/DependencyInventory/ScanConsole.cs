using System.Net;
using Spectre.Console;

namespace DependencyInventory;

/// <summary>Console rendering of a scan: the only place, with the prompts of RepositoryCommand, that uses AnsiConsole.</summary>
public static class ScanConsole
{
    public static void Header()
    {
        AnsiConsole.Write(new Rule("[cyan]Dependency Inventory[/]").LeftJustified());
        AnsiConsole.MarkupLine("Read-only scan; [bold]one branch[/] per repository.");
    }

    public static void FeedHeader()
    {
        AnsiConsole.Write(new Rule("[cyan]Dependency Inventory[/]").LeftJustified());
        AnsiConsole.MarkupLine("Read-only scan of the [bold]published[/] package versions.");
    }

    public static void Resolving() =>
        AnsiConsole.MarkupLine("Listing the package versions to read...");

    public static void Feed(string feed, int versions, int unknownPackages) =>
        AnsiConsole.MarkupLine($"Feed: [blue]{Markup.Escape(feed)}[/] — {versions} package versions to read" +
            (unknownPackages > 0 ? $", {unknownPackages} packages not found." : "."));

    public static void Filters(DependencyMatcher matcher) =>
        AnsiConsole.MarkupLine("Filters (logical OR): " +
            Markup.Escape(string.Join(", ", matcher.Filters)));

    public static void Source(string origin, int accessible, int selected) =>
        AnsiConsole.MarkupLine($"Source: [blue]{Markup.Escape(origin)}[/] — " +
            $"{accessible} accessible repositories, {selected} to scan.");

    public static void OrganizationCredentials(string organization) =>
        AnsiConsole.MarkupLine($"Credentials for the feeds of the Azure DevOps organization [blue]{Markup.Escape(organization)}[/]:");

    public static void PackageSources(IReadOnlyList<string> sources) =>
        AnsiConsole.MarkupLine("Package sources (in order): " + Markup.Escape(string.Join(", ", sources)));

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
        if (references.Any(x => x.Usage is not null))
            foreach (var usage in Enum.GetValues<PackageUsage>())
                summary.AddRow($"{usage} references", references.Count(x => x.Usage == usage).ToString());
        AnsiConsole.Write(summary);
        AnsiConsole.MarkupLine($"Reports: [green]{Markup.Escape(reportDirectory)}[/]");
        if (result.IncompleteRepositories > 0)
            AnsiConsole.MarkupLine("[yellow]Incomplete scan: see repositories.csv.[/]");
    }

    public static void FrameworkSummary(int analysed, FrameworkScanResult result, string reportDirectory)
    {
        var projects = result.Frameworks.Select(x => (x.Project, x.Repository, x.ProjectFile)).Distinct().Count();
        var unresolved = result.Frameworks.Where(x => x.FrameworkSource == FrameworkSource.Unresolved)
            .Select(x => (x.Project, x.Repository, x.ProjectFile)).Distinct().Count();

        var summary = new Table().Border(TableBorder.Rounded)
            .AddColumn("Measure").AddColumn("Value")
            .AddRow("Repositories scanned", analysed.ToString())
            .AddRow("Projects found", projects.ToString())
            .AddRow("Distinct frameworks", result.Frameworks.Where(x => x.Framework is not null)
                .Select(x => x.Framework).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString())
            .AddRow("Projects with an unresolved framework", unresolved.ToString())
            .AddRow("Failed or partial repositories", result.IncompleteRepositories.ToString());
        AnsiConsole.Write(summary);
        AnsiConsole.MarkupLine($"Reports: [green]{Markup.Escape(reportDirectory)}[/]");
        if (result.IncompleteRepositories > 0)
            AnsiConsole.MarkupLine("[yellow]Incomplete scan: see repositories.csv.[/]");
    }

    /// <summary>Reads the package versions behind a progress bar with one step per version.</summary>
    public static async Task<T> WithFeedProgressAsync<T>(int versions, Func<IProgress<PackageVersion>, Task<T>> read)
    {
        T result = default!;
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Reading", maxValue: Math.Max(versions, 1));
                result = await read(new VersionProgress(task));
            });
        return result;
    }

    public static void FeedSummary(FeedScanResult result, string reportDirectory)
    {
        var read = result.Packages.Where(x => x.Status == PackageStatus.OK).ToArray();
        var summary = new Table().Border(TableBorder.Rounded)
            .AddColumn("Measure").AddColumn("Value")
            .AddRow("Package versions read", read.Length.ToString())
            .AddRow("Packages", read.Select(x => x.Package).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString())
            .AddRow("Distinct frameworks", result.Frameworks.Select(x => x.Framework)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString())
            .AddRow("Dependencies kept", result.Dependencies.Count.ToString())
            .AddRow("Not found", result.NotFound.ToString())
            .AddRow("Failed", result.Failed.ToString());
        AnsiConsole.Write(summary);
        AnsiConsole.MarkupLine($"Reports: [green]{Markup.Escape(reportDirectory)}[/]");
        if (result.NotFound > 0 || result.Failed > 0)
            AnsiConsole.MarkupLine("[yellow]Some package versions could not be read: see packages.csv.[/]");
    }

    // Notified concurrently by the parallel reads.
    private sealed class VersionProgress(ProgressTask task) : IProgress<PackageVersion>
    {
        private readonly Lock _lock = new();

        public void Report(PackageVersion value)
        {
            lock (_lock)
            {
                task.Description = Markup.Escape($"{value.Id} {value.Version}");
                task.Increment(1);
            }
        }
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
