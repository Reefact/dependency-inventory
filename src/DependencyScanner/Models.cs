namespace DependencyScanner;

public sealed record AzureOrigin(string Organization, string Project)
{
    public string RootUrl => $"https://dev.azure.com/{Uri.EscapeDataString(Organization)}/{Uri.EscapeDataString(Project)}";

    public static AzureOrigin Parse(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("URL attendue : https://dev.azure.com/organisation/projet");

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length != 2 || parts.Any(x => string.IsNullOrWhiteSpace(x) || x is "." or ".." || x.Contains('/') || x.Contains('\\')))
            throw new ArgumentException("Indiquer l'URL du projet, sans nom de dépôt Git.");
        return new AzureOrigin(parts[0], parts[1]);
    }
}

public sealed record Repository(string Id, string Name, string Project, string WebUrl, bool IsDisabled);
public sealed record GitBranch(string Name, string ObjectId);
public sealed record GitFile(string Path, bool IsFolder);
public sealed record ParsedPackage(string Id, string? Version, string Kind, string VersionSource);

public sealed record PackageOccurrence(
    string Project,
    string Repository,
    string Branch,
    string Commit,
    string File,
    string Package,
    string? Version,
    string Kind,
    string VersionSource,
    IReadOnlyList<string> MatchedFilters,
    string Url);

public sealed record RepositoryCoverage(
    string Project,
    string Repository,
    string? Branch,
    string Status,
    int FilesExamined,
    int References,
    string? Details);

public sealed record ScanReport(
    DateTimeOffset ScannedAt,
    string Origin,
    IReadOnlyList<DependencyFilter> Filters,
    IReadOnlyList<PackageOccurrence> Occurrences,
    IReadOnlyList<RepositoryCoverage> Coverage);
