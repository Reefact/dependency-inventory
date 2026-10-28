using System.Text;
using System.Text.Json;

namespace DependencyInventory;

/// <summary>
/// The CSV format of the reports: UTF-8 with BOM, a value quoted when it holds the separator, a quote or a line break.
/// </summary>
public static class CsvFormat
{
    /// <summary>The separator --csv-delimiter names: "," (the default), ";" or "tab" (also written \t).</summary>
    /// <exception cref="ArgumentException">Any other value.</exception>
    public static char Delimiter(string? name) => name?.ToLowerInvariant() switch
    {
        null or "," => ',',
        ";" => ';',
        "tab" or "\\t" or "\t" => '\t',
        _ => throw new ArgumentException($"--csv-delimiter expects , ; or tab, not '{name}'.")
    };
}

public static class ReportWriter
{
    public static Task<string> SaveAsync(ScanReport report, string directory, CancellationToken ct) =>
        SaveAsync(report, directory, false, ct);

    public static Task<string> SaveAsync(ScanReport report, string directory, bool usedTypes, CancellationToken ct) =>
        SaveAsync(report, directory, usedTypes, ',', ct);

    /// <summary>
    /// Writes the package inventory: report.json, dependencies.csv, central-versions.csv, repositories.csv,
    /// used-types.csv with <paramref name="usedTypes"/>, and transitive-dependencies.csv when the report has them.
    /// </summary>
    public static async Task<string> SaveAsync(ScanReport report, string directory, bool usedTypes, char delimiter,
        CancellationToken ct)
    {
        var absolute = Path.GetFullPath(directory);
        Directory.CreateDirectory(absolute);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(absolute, "report.json"),
            json, new UTF8Encoding(false), ct);

        var references = report.Occurrences.Where(x => x.Kind == PackageKind.Reference).ToArray();
        await WriteCsvAsync(Path.Combine(absolute, "dependencies.csv"), delimiter,
            ["Scope", "Repository", "Branch", "Commit", "File", "ProjectType", "Package", "Version", "VersionSource",
                "MatchedFilters", "Usage", "Source", "Feed", "Url"],
            references.Select(x => new[] { x.Scope, x.Repository, x.Branch, x.Commit, x.File,
                x.ProjectType?.ToString() ?? "", x.Package, x.Version ?? "", x.VersionSource.ToString(),
                string.Join(" | ", x.MatchedFilters), x.Usage?.ToString() ?? "", x.Source?.ToString() ?? "",
                x.Feed ?? "", x.Url }), ct);
        await WriteCsvAsync(Path.Combine(absolute, "central-versions.csv"), delimiter,
            ["Scope", "Repository", "Branch", "Commit", "File", "Package", "Version", "VersionSource", "MatchedFilters",
                "Url"],
            report.Occurrences.Where(x => x.Kind == PackageKind.CentralVersion).Select(x => new[] { x.Scope,
                x.Repository, x.Branch, x.Commit, x.File, x.Package, x.Version ?? "", x.VersionSource.ToString(),
                string.Join(" | ", x.MatchedFilters), x.Url }), ct);
        await WriteCsvAsync(Path.Combine(absolute, "repositories.csv"), delimiter,
            ["Scope", "Repository", "Branch", "Commit", "CommitDate", "Status", "FilesExamined", "References", "Details",
                "Url"],
            report.Coverage.Select(x => new[] { x.Scope, x.Repository, x.Branch ?? "", x.Commit ?? "",
                Date(x.CommitDate), x.Status.ToString(), x.FilesExamined.ToString(), x.References.ToString(),
                x.Details ?? "", x.Url ?? "" }), ct);
        // Without --used-types, the file of a previous run in the same folder would look like part of this report.
        var usedTypesFile = Path.Combine(absolute, "used-types.csv");
        if (!usedTypes) File.Delete(usedTypesFile);
        else
            await WriteCsvAsync(usedTypesFile, delimiter,
                ["Scope", "Repository", "Branch", "Commit", "File", "Package", "Version", "Source", "Type", "Files"],
                references.SelectMany(x => (x.UsedTypes ?? []).Select(t => new[] { x.Scope, x.Repository, x.Branch,
                    x.Commit, x.File, x.Package, x.Version ?? "", x.Source?.ToString() ?? "", t.Type,
                    t.Files.ToString() })), ct);

        // Only with --transitive; the file of a previous run in the same folder is removed otherwise.
        var transitiveFile = Path.Combine(absolute, "transitive-dependencies.csv");
        if (report.TransitiveDependencies is not { } transitive) File.Delete(transitiveFile);
        else
            await WriteCsvAsync(transitiveFile, delimiter,
                ["Scope", "Repository", "Branch", "Commit", "File", "ProjectType", "Framework", "Package", "Version",
                    "Via", "Resolution", "MatchedFilters", "Usage", "Source", "Feed"],
                transitive.Select(x => new[] { x.Scope, x.Repository, x.Branch, x.Commit, x.File,
                    x.ProjectType?.ToString() ?? "", x.Framework ?? "", x.Package, x.Version, x.Via,
                    x.Resolution.ToString(), string.Join(" | ", x.MatchedFilters), x.Usage?.ToString() ?? "",
                    x.Source?.ToString() ?? "", x.Feed ?? "" }), ct);
        return absolute;
    }

    public static Task<string> SaveFrameworksAsync(FrameworkReport report, string directory, CancellationToken ct) =>
        SaveFrameworksAsync(report, directory, ',', ct);

    /// <summary>Writes the frameworks inventory: report.json, frameworks.csv and repositories.csv.</summary>
    public static async Task<string> SaveFrameworksAsync(FrameworkReport report, string directory, char delimiter,
        CancellationToken ct)
    {
        var absolute = Path.GetFullPath(directory);
        Directory.CreateDirectory(absolute);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(absolute, "report.json"),
            json, new UTF8Encoding(false), ct);

        await WriteCsvAsync(Path.Combine(absolute, "frameworks.csv"), delimiter,
            ["Scope", "Repository", "Branch", "Commit", "ProjectFile", "ProjectType", "Framework", "FrameworkSource",
                "Url"],
            report.Frameworks.Select(x => new[] { x.Scope, x.Repository, x.Branch, x.Commit, x.ProjectFile,
                x.ProjectType?.ToString() ?? "", x.Framework ?? "", x.FrameworkSource.ToString(), x.Url }), ct);
        await WriteCsvAsync(Path.Combine(absolute, "repositories.csv"), delimiter,
            ["Scope", "Repository", "Branch", "Commit", "CommitDate", "Status", "FilesExamined", "Projects", "Details",
                "Url"],
            report.Coverage.Select(x => new[] { x.Scope, x.Repository, x.Branch ?? "", x.Commit ?? "",
                Date(x.CommitDate), x.Status.ToString(), x.FilesExamined.ToString(), x.Projects.ToString(),
                x.Details ?? "", x.Url ?? "" }), ct);
        return absolute;
    }

    public static Task<string> SaveFeedAsync(FeedReport report, string directory, CancellationToken ct) =>
        SaveFeedAsync(report, directory, ',', ct);

    /// <summary>
    /// Writes the feed inventory: report.json, package-frameworks.csv, package-dependencies.csv and packages.csv.
    /// </summary>
    public static async Task<string> SaveFeedAsync(FeedReport report, string directory, char delimiter,
        CancellationToken ct)
    {
        var absolute = Path.GetFullPath(directory);
        Directory.CreateDirectory(absolute);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(absolute, "report.json"),
            json, new UTF8Encoding(false), ct);

        await WriteCsvAsync(Path.Combine(absolute, "package-frameworks.csv"), delimiter,
            ["Package", "Version", "Framework"],
            report.Frameworks.Select(x => new[] { x.Package, x.Version, x.Framework }), ct);
        await WriteCsvAsync(Path.Combine(absolute, "package-dependencies.csv"), delimiter,
            ["Package", "Version", "Framework", "Dependency", "VersionRange", "Kind"],
            report.Dependencies.Select(x => new[] { x.Package, x.Version, x.Framework, x.Dependency,
                x.VersionRange ?? "", x.Kind.ToString() }), ct);
        await WriteCsvAsync(Path.Combine(absolute, "packages.csv"), delimiter,
            ["Package", "Version", "Status", "Details", "FrameworksFrom", "Published", "Listed", "Views",
                "Id", "Title", "Authors", "Owners", "Description", "Summary", "ReleaseNotes", "Copyright", "Language",
                "Tags", "ProjectUrl", "IconUrl", "Icon", "Readme", "License", "LicenseType", "LicenseUrl",
                "RequireLicenseAcceptance", "DevelopmentDependency", "Serviceable", "MinClientVersion", "PackageTypes",
                "RepositoryType", "RepositoryUrl", "RepositoryBranch", "RepositoryCommit"],
            report.Packages.Select(PackageColumns), ct);
        return absolute;
    }

    /// <summary>Writes diagnostics.json in a report directory returned by a Save method.</summary>
    public static Task SaveDiagnosticsAsync<T>(
        IReadOnlyList<T> diagnostics, string directory, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true });
        return File.WriteAllTextAsync(Path.Combine(directory, "diagnostics.json"), json, ct);
    }

    private static string[] PackageColumns(PackageInfo x)
    {
        var m = x.Metadata;
        return [x.Package, x.Version, x.Status.ToString(), x.Details ?? "", x.FrameworksFrom?.ToString() ?? "",
            Date(x.Published),
            x.Listed switch { true => "true", false => "false", null => "" }, string.Join(" | ", x.Views ?? []),
            m?.Id ?? "", m?.Title ?? "", m?.Authors ?? "", m?.Owners ?? "", m?.Description ?? "", m?.Summary ?? "",
            m?.ReleaseNotes ?? "", m?.Copyright ?? "", m?.Language ?? "", m?.Tags ?? "", m?.ProjectUrl ?? "",
            m?.IconUrl ?? "", m?.Icon ?? "", m?.Readme ?? "", m?.License ?? "", m?.LicenseType ?? "",
            m?.LicenseUrl ?? "", m?.RequireLicenseAcceptance ?? "", m?.DevelopmentDependency ?? "",
            m?.Serviceable ?? "", m?.MinClientVersion ?? "", m?.PackageTypes ?? "", m?.RepositoryType ?? "",
            m?.RepositoryUrl ?? "", m?.RepositoryBranch ?? "", m?.RepositoryCommit ?? ""];
    }

    // ISO 8601 in UTC, to the second: 2026-10-28T09:42:56Z.
    private static string Date(DateTimeOffset? date) =>
        date?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)
        ?? "";

    private static async Task WriteCsvAsync(string path, char delimiter, IEnumerable<string> header,
        IEnumerable<string[]> rows, CancellationToken ct)
    {
        // The BOM lets Excel read the file as UTF-8.
        var lines = new[] { string.Join(delimiter, header.Select(h => Escape(h, delimiter))) }
            .Concat(rows.Select(row => string.Join(delimiter, row.Select(v => Escape(v, delimiter)))));
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(true), ct);
    }

    private static string Escape(string input, char delimiter) =>
        input.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0
            ? "\"" + input.Replace("\"", "\"\"") + "\"" : input;
}
