namespace DependencyInventory;

/// <summary>Notified around each repository scan, e.g. to drive a progress bar.</summary>
public interface IScanProgress
{
    void RepositoryStarted(Repository repository);
    void RepositoryCompleted(Repository repository);
}

public sealed record RepositoryScanResult(
    RepositoryCoverage Coverage,
    IReadOnlyList<PackageOccurrence> Occurrences,
    IReadOnlyList<TransitiveDependency>? TransitiveDependencies = null);

/// <summary>The scan of every repository; transitive dependencies only when they were resolved.</summary>
public sealed record ScanResult(
    IReadOnlyList<PackageOccurrence> Occurrences,
    IReadOnlyList<RepositoryCoverage> Coverage,
    IReadOnlyList<RepositoryDiagnostic> Diagnostics,
    IReadOnlyList<TransitiveDependency>? TransitiveDependencies = null)
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

/// <summary>
/// What could be read in one repository; no branch means none of the requested ones exists, for the reason given.
/// The tree lists every file of the commit, read or not.
/// </summary>
internal sealed record RepositoryRead<T>(GitBranch? Branch, IReadOnlyList<FileRead<T>> Files,
    IReadOnlyList<string>? Tree = null, string? NoBranchReason = null);

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
    /// Reads and parses the files of the branch selected by <paramref name="branchPolicy"/> that
    /// <paramref name="isInteresting"/> keeps. A file that cannot be read or parsed is returned with its error; a
    /// branch or tree failure is thrown. <paramref name="branchesListed"/> is called as soon as the branch is
    /// selected, before the tree is read.
    /// </summary>
    public static async Task<RepositoryRead<T>> ReadAsync<T>(IRepositoryProvider api, Repository repo,
        BranchPolicy branchPolicy, Func<string, bool> isInteresting, Func<string, string, T> parse,
        Action<IReadOnlyList<GitBranch>, GitBranch?> branchesListed, int fileParallelism, CancellationToken ct)
    {
        var branches = await api.GetBranchesAsync(repo.Id, ct);
        var (requested, section) = branchPolicy.For(repo.Name);
        var branch = BranchSelector.Select(branches, requested, repo.DefaultBranch);
        branchesListed(branches, branch);
        if (branch is null) return new(null, [], null, NoBranchReason(branches, requested, section, repo));

        var tree = (await api.GetTreeAsync(repo.Id, branch.ObjectId, ct)).Where(x => !x.IsFolder)
            .Select(x => x.Path).ToArray();
        var paths = tree.Where(isInteresting).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(branch, await ReadFilesAsync(api, repo, branch.ObjectId, paths, parse, fileParallelism, ct), tree);
    }

    private static string NoBranchReason(IReadOnlyList<GitBranch> branches, IReadOnlyList<string>? requested,
        string? section, Repository repo)
    {
        if (branches.Count == 0) return "No Git branch";
        if (requested is null)
            return repo.DefaultBranch is null ? "No default branch" : $"Default branch not found: {repo.DefaultBranch}";
        return $"None of the requested branches exists: {string.Join(", ", requested)}" +
            (section is null ? "" : $" (section [{section}])");
    }

    /// <summary>
    /// Reads and parses the given files of a commit, in their order. A file that cannot be read or parsed is
    /// returned with its error.
    /// </summary>
    public static async Task<IReadOnlyList<FileRead<T>>> ReadFilesAsync<T>(IRepositoryProvider api, Repository repo,
        string commitId, IReadOnlyList<string> paths, Func<string, string, T> parse, int fileParallelism,
        CancellationToken ct)
    {
        // Every file comes from this single commit.
        var reads = new FileRead<T>[paths.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Count),
            new ParallelOptions { MaxDegreeOfParallelism = fileParallelism, CancellationToken = ct },
            async (index, token) =>
            {
                var path = paths[index];
                try
                {
                    var content = await api.GetFileAsync(repo.Id, commitId, path, token);
                    reads[index] = new FileRead<T>(path, content.Length, parse(path, content), null);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    reads[index] = new FileRead<T>(path, null, default, ex.Message);
                }
            });
        return reads;
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
    /// With <paramref name="usage"/>, every reference also gets the usage of its package by the code; with
    /// <paramref name="transitive"/>, the transitive dependencies matching the filters are resolved too.
    /// </summary>
    public static async Task<ScanResult> ScanAsync(IRepositoryProvider api,
        IReadOnlyList<Repository> repositories, DependencyMatcher matcher, bool collectDiagnostics,
        ScanParallelism parallelism, IScanProgress? progress, CancellationToken ct,
        PackageUsageAnalyzer? usage = null, TransitiveResolver? transitive = null, BranchPolicy? branches = null)
    {
        var results = await RepositoryWalker.ForEachAsync(repositories, parallelism, progress,
            async (repo, fileDegree, token) =>
            {
                var diagnostic = collectDiagnostics
                    ? new RepositoryDiagnostic { Project = repo.Project, Repository = repo.Name }
                    : null;
                var scan = await ScanRepositoryAsync(api, repo, matcher, diagnostic, token, fileDegree, usage,
                    transitive, branches);
                return (Scan: scan, Diagnostic: diagnostic);
            }, ct);

        return new ScanResult(
            results.SelectMany(x => x.Scan.Occurrences).ToArray(),
            results.Select(x => x.Scan.Coverage).ToArray(),
            results.Where(x => x.Diagnostic is not null).Select(x => x.Diagnostic!).ToArray(),
            transitive is null ? null : results.SelectMany(x => x.Scan.TransitiveDependencies ?? []).ToArray());
    }

    /// <summary>
    /// Scans one repository on its selected branch. Failures are reported in the coverage,
    /// never thrown, except cancellation.
    /// </summary>
    public static async Task<RepositoryScanResult> ScanRepositoryAsync(IRepositoryProvider api,
        Repository repo, DependencyMatcher matcher, RepositoryDiagnostic? diagnostic, CancellationToken ct,
        int fileParallelism = 1, PackageUsageAnalyzer? usage = null, TransitiveResolver? transitive = null,
        BranchPolicy? branches = null)
    {
        IReadOnlyList<PackageOccurrence> hits = [];
        IReadOnlyList<TransitiveDependency> transitiveDependencies = [];
        if (repo.IsDisabled)
            return new(new(repo.Project, repo.Name, null, CoverageStatus.Disabled, 0, 0, "Repository disabled"), hits);

        string? branchName = null;
        int filesRead = 0;
        var failures = new List<string>();
        try
        {
            var read = await RepositoryWalker.ReadAsync(api, repo, branches ?? BranchPolicy.RepositoryDefault,
                NuGetDependencyParser.IsInterestingFile,
                MsBuildFile.Parse,
                (branches, selected) =>
                {
                    branchName = selected?.Name;
                    if (diagnostic is null) return;
                    diagnostic.AvailableBranches.AddRange(branches.Select(b => b.Name));
                    diagnostic.SelectedBranch = selected?.Name;
                    diagnostic.Commit = selected?.ObjectId;
                }, fileParallelism, ct);
            if (read.Branch is not { } branch)
                return new(new(repo.Project, repo.Name, null, CoverageStatus.NoBranch, 0, 0, read.NoBranchReason), hits);

            // Git paths are case-sensitive: two paths differing only by case are two files.
            var msbuildFiles = new Dictionary<string, MsBuildFile>(StringComparer.Ordinal);
            foreach (var file in read.Files)
            {
                diagnostic?.Files.Add(new FileDiagnostic(file.Path, file.ContentLength, file.Parsed?.Packages ?? [],
                    file.Error));
                if (file.Error is null)
                {
                    msbuildFiles[file.Path] = file.Parsed!;
                    filesRead++;
                }
                else failures.Add($"{file.Path}: {file.Error}");
            }
            var parsedFiles = msbuildFiles.ToDictionary(f => f.Key, f => f.Value.Packages, StringComparer.Ordinal);

            var found = new List<PackageOccurrence>();
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
                found.Add(new PackageOccurrence(repo.Project, repo.Name, branch.Name,
                    branch.ObjectId, file.Key, package.Id, version, package.Kind, source,
                    matchedFilters, api.FileUrl(repo, branch.ObjectId, file.Key)));
            }
            hits = found;
            // Every declared reference takes part in the graph, matching the filters or not; a lock file may list
            // packages even without any (implicit references of the SDK).
            if (transitive is not null)
                transitiveDependencies = await transitive.ResolveAsync(api, repo, branch, read.Tree ?? [],
                    msbuildFiles, matcher, diagnostic, fileParallelism, ct);
            if (usage is not null)
                (hits, transitiveDependencies) = await AnalyzeUsageAsync(usage, api, repo, branch, read.Tree ?? [],
                    msbuildFiles, found, transitiveDependencies, diagnostic, fileParallelism, ct);
            var status = failures.Count > 0 ? CoverageStatus.Partial
                : read.Files.Count == 0 ? CoverageStatus.NoNuGetFiles : CoverageStatus.OK;
            return new(new(repo.Project, repo.Name, branchName, status,
                filesRead, hits.Count(x => x.Kind == PackageKind.Reference),
                failures.Count > 0 ? string.Join(" | ", failures.Take(5)) : null), hits, transitiveDependencies);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(new(repo.Project, repo.Name, branchName, CoverageStatus.Failed, filesRead, 0, ex.Message), hits,
                transitiveDependencies);
        }
    }

    /// <summary>
    /// The usage of the declared references and of the transitive dependencies, analyzed together so the source files
    /// are read once. A transitive dependency is analyzed in its project at its resolved version.
    /// </summary>
    private static async Task<(IReadOnlyList<PackageOccurrence>, IReadOnlyList<TransitiveDependency>)>
        AnalyzeUsageAsync(PackageUsageAnalyzer usage, IRepositoryProvider api, Repository repo, GitBranch branch,
            IReadOnlyList<string> tree, IReadOnlyDictionary<string, MsBuildFile> files,
            IReadOnlyList<PackageOccurrence> found, IReadOnlyList<TransitiveDependency> transitive,
            RepositoryDiagnostic? diagnostic, int fileParallelism, CancellationToken ct)
    {
        var undeclared = transitive.DistinctBy(TransitiveKey)
            .Select(t => new PackageOccurrence(t.Project, t.Repository, t.Branch, t.Commit, t.File, t.Package,
                $"[{t.Version}]", PackageKind.Reference, VersionSource.Explicit, t.MatchedFilters,
                api.FileUrl(repo, branch.ObjectId, t.File)))
            .ToArray();
        if (!found.Any(x => x.Kind == PackageKind.Reference) && undeclared.Length == 0) return (found, transitive);

        var analyzed = await usage.AnalyzeAsync(api, repo, branch.ObjectId, tree, files, [.. found, .. undeclared],
            diagnostic, fileParallelism, ct);
        var byKey = analyzed.Skip(found.Count)
            .ToDictionary(o => (o.File, o.Package.ToLowerInvariant(), o.Version!.Trim('[', ']')));
        return (analyzed.Take(found.Count).ToArray(), transitive.Select(t => byKey[TransitiveKey(t)] is var o
            ? t with { Usage = o.Usage, Source = o.Source, Feed = o.Feed, UsedTypes = o.UsedTypes }
            : t).ToArray());
    }

    private static (string, string, string) TransitiveKey(TransitiveDependency t) =>
        (t.File, t.Package.ToLowerInvariant(), t.Version);
}
