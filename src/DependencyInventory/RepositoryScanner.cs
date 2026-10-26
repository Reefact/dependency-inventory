namespace DependencyInventory;

/// <summary>Notified around each repository scan, e.g. to drive a progress bar.</summary>
public interface IScanProgress
{
    void RepositoryStarted(Repository repository);
    void RepositoryCompleted(Repository repository);
}

public sealed record RepositoryScanResult(
    RepositoryCoverage Coverage,
    IReadOnlyList<PackageOccurrence> Occurrences);

public sealed record ScanResult(
    IReadOnlyList<PackageOccurrence> Occurrences,
    IReadOnlyList<RepositoryCoverage> Coverage,
    IReadOnlyList<RepositoryDiagnostic> Diagnostics)
{
    /// <summary>Repositories that could not be fully inspected.</summary>
    public int IncompleteRepositories =>
        Coverage.Count(x => x.Status is CoverageStatus.Partial or CoverageStatus.Failed);
}

/// <summary>Scans repositories through a provider; no console access, so it can be tested directly.</summary>
public static class RepositoryScanner
{
    /// <summary>All repositories, or only the one named (case-insensitive), ordered by name.</summary>
    public static IReadOnlyList<Repository> Select(IEnumerable<Repository> repositories, string? name) =>
        repositories
            .Where(r => name is null || r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static async Task<ScanResult> ScanAsync(IRepositoryProvider api,
        IReadOnlyList<Repository> repositories, DependencyMatcher matcher, bool collectDiagnostics,
        IScanProgress? progress, CancellationToken ct)
    {
        var occurrences = new List<PackageOccurrence>();
        var coverage = new List<RepositoryCoverage>();
        var diagnostics = new List<RepositoryDiagnostic>();
        foreach (var repo in repositories)
        {
            ct.ThrowIfCancellationRequested();
            progress?.RepositoryStarted(repo);
            var diagnostic = collectDiagnostics
                ? new RepositoryDiagnostic { Project = repo.Project, Repository = repo.Name }
                : null;
            var result = await ScanRepositoryAsync(api, repo, matcher, diagnostic, ct);
            occurrences.AddRange(result.Occurrences);
            coverage.Add(result.Coverage);
            if (diagnostic is not null) diagnostics.Add(diagnostic);
            progress?.RepositoryCompleted(repo);
        }
        return new ScanResult(occurrences, coverage, diagnostics);
    }

    /// <summary>
    /// Scans one repository on its selected branch. Failures are reported in the coverage,
    /// never thrown, except cancellation.
    /// </summary>
    public static async Task<RepositoryScanResult> ScanRepositoryAsync(IRepositoryProvider api,
        Repository repo, DependencyMatcher matcher, RepositoryDiagnostic? diagnostic, CancellationToken ct)
    {
        var hits = new List<PackageOccurrence>();
        if (repo.IsDisabled)
            return new(new(repo.Project, repo.Name, null, CoverageStatus.Disabled, 0, 0, "Dépôt désactivé"), hits);

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
                return new(new(repo.Project, repo.Name, null, CoverageStatus.NoBranch, 0, 0, "Aucune branche Git"), hits);
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
                    NuGetDependencyParser.IsProjectFile(file.Key))
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
            return new(new(repo.Project, repo.Name, branchName, status,
                filesRead, hits.Count(x => x.Kind == PackageKind.Reference),
                failures.Count > 0 ? string.Join(" | ", failures.Take(5)) : null), hits);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new(new(repo.Project, repo.Name, branchName, CoverageStatus.Failed, filesRead, 0, ex.Message), hits);
        }
    }
}
