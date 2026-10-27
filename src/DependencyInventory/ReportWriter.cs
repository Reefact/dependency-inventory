using System.Text;
using System.Text.Json;

namespace DependencyInventory;

public static class ReportWriter
{
    public static async Task<string> SaveAsync(ScanReport report, string directory, CancellationToken ct)
    {
        var absolute = Path.GetFullPath(directory);
        Directory.CreateDirectory(absolute);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(absolute, "report.json"),
            json, new UTF8Encoding(false), ct);

        var columns = new[]
        {
            "Project", "Repository", "Branch", "Commit", "File", "Package",
            "Version", "Kind", "VersionSource", "MatchedFilters", "Url"
        };
        await WriteCsvAsync(Path.Combine(absolute, "dependencies.csv"), columns,
            report.Occurrences.Where(x => x.Kind == PackageKind.Reference).Select(ToColumns), ct);
        await WriteCsvAsync(Path.Combine(absolute, "central-versions.csv"), columns,
            report.Occurrences.Where(x => x.Kind == PackageKind.CentralVersion).Select(ToColumns), ct);
        await WriteCsvAsync(Path.Combine(absolute, "repositories.csv"),
            ["Project", "Repository", "Branch", "Status", "FilesExamined", "References", "Details"],
            report.Coverage.Select(x => new[] { x.Project, x.Repository, x.Branch ?? "", x.Status.ToString(),
                x.FilesExamined.ToString(), x.References.ToString(), x.Details ?? "" }), ct);
        return absolute;
    }

    /// <summary>Writes the frameworks inventory: report.json, frameworks.csv and repositories.csv.</summary>
    public static async Task<string> SaveFrameworksAsync(FrameworkReport report, string directory, CancellationToken ct)
    {
        var absolute = Path.GetFullPath(directory);
        Directory.CreateDirectory(absolute);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(absolute, "report.json"),
            json, new UTF8Encoding(false), ct);

        await WriteCsvAsync(Path.Combine(absolute, "frameworks.csv"),
            ["Project", "Repository", "Branch", "Commit", "ProjectFile", "Framework", "FrameworkSource", "Url"],
            report.Frameworks.Select(x => new[] { x.Project, x.Repository, x.Branch, x.Commit, x.ProjectFile,
                x.Framework ?? "", x.FrameworkSource.ToString(), x.Url }), ct);
        await WriteCsvAsync(Path.Combine(absolute, "repositories.csv"),
            ["Project", "Repository", "Branch", "Status", "FilesExamined", "Projects", "Details"],
            report.Coverage.Select(x => new[] { x.Project, x.Repository, x.Branch ?? "", x.Status.ToString(),
                x.FilesExamined.ToString(), x.Projects.ToString(), x.Details ?? "" }), ct);
        return absolute;
    }

    /// <summary>Writes diagnostics.json in a report directory returned by a Save method.</summary>
    public static Task SaveDiagnosticsAsync<T>(
        IReadOnlyList<T> diagnostics, string directory, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true });
        return File.WriteAllTextAsync(Path.Combine(directory, "diagnostics.json"), json, ct);
    }

    private static string[] ToColumns(PackageOccurrence x) =>
        [x.Project, x.Repository, x.Branch, x.Commit, x.File,
            x.Package, x.Version ?? "", x.Kind.ToString(), x.VersionSource.ToString(),
            string.Join(" | ", x.MatchedFilters), x.Url];

    private static async Task WriteCsvAsync(
        string path, IEnumerable<string> header, IEnumerable<string[]> rows, CancellationToken ct)
    {
        // Semicolon-delimited with UTF-8 BOM for convenient use in French Excel.
        var lines = new[] { string.Join(";", header.Select(Escape)) }
            .Concat(rows.Select(row => string.Join(";", row.Select(Escape))));
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(true), ct);
    }

    private static string Escape(string input) =>
        input.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0
            ? "\"" + input.Replace("\"", "\"\"") + "\"" : input;
}
