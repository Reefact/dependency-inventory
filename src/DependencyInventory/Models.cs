using System.Text.Json.Serialization;

namespace DependencyInventory;

public sealed record AzureOrigin(string Organization, string Project)
{
    public string RootUrl => $"https://dev.azure.com/{Uri.EscapeDataString(Organization)}/{Uri.EscapeDataString(Project)}";

    public static AzureOrigin Parse(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Expected URL: https://dev.azure.com/organization/project");

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length != 2 || parts.Any(x => string.IsNullOrWhiteSpace(x) || x is "." or ".." || x.Contains('/') || x.Contains('\\')))
            throw new ArgumentException("Give the project URL, without a Git repository name.");
        return new AzureOrigin(parts[0], parts[1]);
    }
}

public sealed record Repository(string Id, string Name, string Project, string WebUrl, bool IsDisabled);
public sealed record GitBranch(string Name, string ObjectId);
public sealed record GitFile(string Path, bool IsFolder);
// Enum names are written as-is in the CSV and JSON reports: renaming a member changes the output format.
[JsonConverter(typeof(JsonStringEnumConverter<PackageKind>))]
public enum PackageKind { Reference, CentralVersion }

[JsonConverter(typeof(JsonStringEnumConverter<VersionSource>))]
public enum VersionSource { Explicit, Unresolved, Central }

[JsonConverter(typeof(JsonStringEnumConverter<CoverageStatus>))]
public enum CoverageStatus { OK, Partial, Failed, NoBranch, Disabled, NoNuGetFiles }

public sealed record ParsedPackage(string Id, string? Version, PackageKind Kind, VersionSource VersionSource);

public sealed record PackageOccurrence(
    string Project,
    string Repository,
    string Branch,
    string Commit,
    string File,
    string Package,
    string? Version,
    PackageKind Kind,
    VersionSource VersionSource,
    IReadOnlyList<string> MatchedFilters,
    string Url);

public sealed record RepositoryCoverage(
    string Project,
    string Repository,
    string? Branch,
    CoverageStatus Status,
    int FilesExamined,
    int References,
    string? Details);

public sealed record ScanReport(
    DateTimeOffset ScannedAt,
    string Origin,
    IReadOnlyList<DependencyFilter> Filters,
    IReadOnlyList<PackageOccurrence> Occurrences,
    IReadOnlyList<RepositoryCoverage> Coverage);
