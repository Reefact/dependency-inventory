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

public enum ParallelismMode
{
    /// <summary>One repository at a time, several of its files read at once.</summary>
    Files,

    /// <summary>Several repositories at once, each reading its files one after the other.</summary>
    Repositories
}

/// <summary>
/// How many requests may run at once (--parallelism) and at which level (--parallelism-mode). Only one
/// level runs in parallel, so the degree is also the maximum number of simultaneous HTTP requests.
/// </summary>
public sealed record ScanParallelism
{
    public const int MaxDegree = 8;

    public static readonly ScanParallelism Sequential = new(1, ParallelismMode.Files);

    public int Degree { get; }
    public ParallelismMode Mode { get; }

    private ScanParallelism(int degree, ParallelismMode mode)
    {
        Degree = degree;
        Mode = mode;
    }

    internal int RepositoryDegree => Mode == ParallelismMode.Repositories ? Degree : 1;
    internal int FileDegree => Mode == ParallelismMode.Files ? Degree : 1;

    /// <summary>Validates the two options together; the mode is required above 1 and refused at 1.</summary>
    public static ScanParallelism Create(int degree, ParallelismMode? mode)
    {
        if (degree is < 1 or > MaxDegree)
            throw new ArgumentException($"--parallelism must be between 1 and {MaxDegree}.");
        // The CLI also converts numbers to the enum, so an undefined value can reach this point.
        if (mode is { } given && !Enum.IsDefined(given))
            throw new ArgumentException("--parallelism-mode accepts files or repositories.");
        if (degree == 1)
            return mode is null
                ? Sequential
                : throw new ArgumentException("--parallelism-mode only applies when --parallelism is above 1.");
        return mode is { } value
            ? new ScanParallelism(degree, value)
            : throw new ArgumentException(
                "--parallelism-mode is required when --parallelism is above 1: files or repositories.");
    }
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

    /// <summary>
    /// Scans the repositories; the result lists them in the given order whatever the parallelism.
    /// The progress is notified concurrently in <see cref="ParallelismMode.Repositories"/> mode.
    /// </summary>
    public static async Task<ScanResult> ScanAsync(IRepositoryProvider api,
        IReadOnlyList<Repository> repositories, DependencyMatcher matcher, bool collectDiagnostics,
        ScanParallelism parallelism, IScanProgress? progress, CancellationToken ct)
    {
        var results = new (RepositoryScanResult Scan, RepositoryDiagnostic? Diagnostic)[repositories.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, repositories.Count),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism.RepositoryDegree, CancellationToken = ct },
            async (index, token) =>
            {
                var repo = repositories[index];
                progress?.RepositoryStarted(repo);
                var diagnostic = collectDiagnostics
                    ? new RepositoryDiagnostic { Project = repo.Project, Repository = repo.Name }
                    : null;
                var scan = await ScanRepositoryAsync(api, repo, matcher, diagnostic, token, parallelism.FileDegree);
                results[index] = (scan, diagnostic);
                progress?.RepositoryCompleted(repo);
            });

        return new ScanResult(
            results.SelectMany(x => x.Scan.Occurrences).ToArray(),
            results.Select(x => x.Scan.Coverage).ToArray(),
            results.Where(x => x.Diagnostic is not null).Select(x => x.Diagnostic!).ToArray());
    }

    /// <summary>
    /// Scans one repository on its selected branch. Failures are reported in the coverage,
    /// never thrown, except cancellation.
    /// </summary>
    public static async Task<RepositoryScanResult> ScanRepositoryAsync(IRepositoryProvider api,
        Repository repo, DependencyMatcher matcher, RepositoryDiagnostic? diagnostic, CancellationToken ct,
        int fileParallelism = 1)
    {
        var hits = new List<PackageOccurrence>();
        if (repo.IsDisabled)
            return new(new(repo.Project, repo.Name, null, CoverageStatus.Disabled, 0, 0, "Repository disabled"), hits);

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
                return new(new(repo.Project, repo.Name, null, CoverageStatus.NoBranch, 0, 0, "No Git branch"), hits);
            branchName = branch.Name;
            if (diagnostic is not null)
            {
                diagnostic.SelectedBranch = branch.Name;
                diagnostic.Commit = branch.ObjectId;
            }
            var tree = await api.GetTreeAsync(repo.Id, branch.ObjectId, ct);
            var paths = tree.Where(x => !x.IsFolder && NuGetDependencyParser.IsInterestingFile(x.Path))
                .Select(x => x.Path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

            // Every reference comes from this single commit.
            // Files may be read in parallel; their results are then taken in path order.
            var reads = new FileDiagnostic[paths.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, paths.Length),
                new ParallelOptions { MaxDegreeOfParallelism = fileParallelism, CancellationToken = ct },
                async (index, token) =>
                {
                    var path = paths[index];
                    try
                    {
                        var content = await api.GetFileAsync(repo.Id, branch.ObjectId, path, token);
                        reads[index] = new FileDiagnostic(path, content.Length,
                            NuGetDependencyParser.Parse(path, content), null);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        reads[index] = new FileDiagnostic(path, null, [], ex.Message);
                    }
                });

            var parsedFiles = new Dictionary<string, IReadOnlyList<ParsedPackage>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var read in reads)
            {
                diagnostic?.Files.Add(read);
                if (read.Error is null)
                {
                    parsedFiles[read.Path] = read.Packages;
                    filesRead++;
                }
                else failures.Add($"{read.Path}: {read.Error}");
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(new(repo.Project, repo.Name, branchName, CoverageStatus.Failed, filesRead, 0, ex.Message), hits);
        }
    }
}
