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

/// <summary>One file of the selected commit: its parsed content, or the error that prevented reading it.</summary>
internal sealed record FileRead<T>(string Path, int? ContentLength, T? Parsed, string? Error);

/// <summary>What could be read in one repository; no branch means the repository has none.</summary>
internal sealed record RepositoryRead<T>(GitBranch? Branch, IReadOnlyList<FileRead<T>> Files);

/// <summary>
/// The walk shared by every inventory: selected branch, tree of its commit, files read (possibly in parallel)
/// and returned in path order. No console access, so it can be tested directly.
/// </summary>
internal static class RepositoryWalker
{
    /// <summary>
    /// Runs <paramref name="scan"/> on every repository; the results keep the given order whatever the parallelism.
    /// The progress is notified concurrently in <see cref="ParallelismMode.Repositories"/> mode.
    /// </summary>
    public static async Task<TResult[]> ForEachAsync<TResult>(IReadOnlyList<Repository> repositories,
        ScanParallelism parallelism, IScanProgress? progress,
        Func<Repository, int, CancellationToken, Task<TResult>> scan, CancellationToken ct)
    {
        var results = new TResult[repositories.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, repositories.Count),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism.RepositoryDegree, CancellationToken = ct },
            async (index, token) =>
            {
                var repo = repositories[index];
                progress?.RepositoryStarted(repo);
                results[index] = await scan(repo, parallelism.FileDegree, token);
                progress?.RepositoryCompleted(repo);
            });
        return results;
    }

    /// <summary>
    /// Reads and parses the files of the selected branch that <paramref name="isInteresting"/> keeps. A file that
    /// cannot be read or parsed is returned with its error; a branch or tree failure is thrown.
    /// <paramref name="branchesListed"/> is called as soon as the branch is selected, before the tree is read.
    /// </summary>
    public static async Task<RepositoryRead<T>> ReadAsync<T>(IRepositoryProvider api, Repository repo,
        Func<string, bool> isInteresting, Func<string, string, T> parse,
        Action<IReadOnlyList<GitBranch>, GitBranch?> branchesListed, int fileParallelism, CancellationToken ct)
    {
        var branches = await api.GetBranchesAsync(repo.Id, ct);
        var branch = BranchSelector.Select(branches);
        branchesListed(branches, branch);
        if (branch is null) return new(null, []);

        var tree = await api.GetTreeAsync(repo.Id, branch.ObjectId, ct);
        var paths = tree.Where(x => !x.IsFolder && isInteresting(x.Path))
            .Select(x => x.Path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

        // Every file comes from this single commit.
        var reads = new FileRead<T>[paths.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Length),
            new ParallelOptions { MaxDegreeOfParallelism = fileParallelism, CancellationToken = ct },
            async (index, token) =>
            {
                var path = paths[index];
                try
                {
                    var content = await api.GetFileAsync(repo.Id, branch.ObjectId, path, token);
                    reads[index] = new FileRead<T>(path, content.Length, parse(path, content), null);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    reads[index] = new FileRead<T>(path, null, default, ex.Message);
                }
            });
        return new(branch, reads);
    }
}

/// <summary>Scans repositories for NuGet packages; no console access, so it can be tested directly.</summary>
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
        var results = await RepositoryWalker.ForEachAsync(repositories, parallelism, progress,
            async (repo, fileDegree, token) =>
            {
                var diagnostic = collectDiagnostics
                    ? new RepositoryDiagnostic { Project = repo.Project, Repository = repo.Name }
                    : null;
                var scan = await ScanRepositoryAsync(api, repo, matcher, diagnostic, token, fileDegree);
                return (Scan: scan, Diagnostic: diagnostic);
            }, ct);

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
            var read = await RepositoryWalker.ReadAsync(api, repo, NuGetDependencyParser.IsInterestingFile,
                NuGetDependencyParser.Parse,
                (branches, selected) =>
                {
                    branchName = selected?.Name;
                    if (diagnostic is null) return;
                    diagnostic.AvailableBranches.AddRange(branches.Select(b => b.Name));
                    diagnostic.SelectedBranch = selected?.Name;
                    diagnostic.Commit = selected?.ObjectId;
                }, fileParallelism, ct);
            if (read.Branch is not { } branch)
                return new(new(repo.Project, repo.Name, null, CoverageStatus.NoBranch, 0, 0, "No Git branch"), hits);

            var parsedFiles = new Dictionary<string, IReadOnlyList<ParsedPackage>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var file in read.Files)
            {
                diagnostic?.Files.Add(new FileDiagnostic(file.Path, file.ContentLength, file.Parsed ?? [], file.Error));
                if (file.Error is null)
                {
                    parsedFiles[file.Path] = file.Parsed!;
                    filesRead++;
                }
                else failures.Add($"{file.Path}: {file.Error}");
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
                : read.Files.Count == 0 ? CoverageStatus.NoNuGetFiles : CoverageStatus.OK;
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
