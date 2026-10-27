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
public enum CoverageStatus { OK, Partial, Failed, NoBranch, Disabled, NoNuGetFiles, NoProjectFiles }

[JsonConverter(typeof(JsonStringEnumConverter<FrameworkSource>))]
public enum FrameworkSource { Project, DirectoryBuildProps, Unresolved }

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

/// <summary>One target framework of one project file; no framework when it could not be resolved.</summary>
public sealed record FrameworkOccurrence(
    string Project,
    string Repository,
    string Branch,
    string Commit,
    string ProjectFile,
    string? Framework,
    FrameworkSource FrameworkSource,
    string Url);

/// <summary>Coverage of one repository by the frameworks inventory.</summary>
public sealed record ProjectCoverage(
    string Project,
    string Repository,
    string? Branch,
    CoverageStatus Status,
    int FilesExamined,
    int Projects,
    string? Details);

public sealed record FrameworkReport(
    DateTimeOffset ScannedAt,
    string Origin,
    IReadOnlyList<FrameworkOccurrence> Frameworks,
    IReadOnlyList<ProjectCoverage> Coverage);

[JsonConverter(typeof(JsonStringEnumConverter<DependencyKind>))]
public enum DependencyKind { Package, FrameworkReference }

[JsonConverter(typeof(JsonStringEnumConverter<PackageStatus>))]
public enum PackageStatus { OK, NotFound, Failed }

/// <summary>One target framework shipped by one published package version.</summary>
public sealed record PackageFramework(string Package, string Version, string Framework);

/// <summary>
/// One dependency of a published package version, for one framework; an empty framework applies to all.
/// A FrameworkReference has no version range: its version is the framework's.
/// </summary>
public sealed record PackageDependency(
    string Package,
    string Version,
    string Framework,
    string Dependency,
    string? VersionRange,
    DependencyKind Kind);

/// <summary>Whether a package version could be read; no version when the package itself is unknown.</summary>
public sealed record PackageCoverage(string Package, string Version, PackageStatus Status, string? Details);

public sealed record FeedReport(
    DateTimeOffset ScannedAt,
    string Feed,
    IReadOnlyList<DependencyFilter> PackageFilters,
    IReadOnlyList<DependencyFilter> DependencyFilters,
    IReadOnlyList<PackageFramework> Frameworks,
    IReadOnlyList<PackageDependency> Dependencies,
    IReadOnlyList<PackageCoverage> Packages);
