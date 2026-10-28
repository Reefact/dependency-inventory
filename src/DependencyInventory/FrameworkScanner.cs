namespace DependencyInventory;

public sealed record FrameworkRepositoryResult(
    ProjectCoverage Coverage,
    IReadOnlyList<FrameworkOccurrence> Frameworks);

public sealed record FrameworkScanResult(
    IReadOnlyList<FrameworkOccurrence> Frameworks,
    IReadOnlyList<ProjectCoverage> Coverage,
    IReadOnlyList<FrameworkRepositoryDiagnostic> Diagnostics)
{
    /// <summary>Repositories that could not be fully inspected.</summary>
    public int IncompleteRepositories =>
        Coverage.Count(x => x.Status is CoverageStatus.Partial or CoverageStatus.Failed);
}

/// <summary>Inventories the target frameworks of every project; no console access, so it can be tested directly.</summary>
public static class FrameworkScanner
{
    /// <summary>
    /// Scans the repositories; the result lists them in the given order whatever the parallelism.
    /// The progress is notified concurrently in <see cref="ParallelismMode.Repositories"/> mode.
    /// </summary>
    public static async Task<FrameworkScanResult> ScanAsync(IRepositoryProvider api,
        IReadOnlyList<Repository> repositories, bool collectDiagnostics,
        ScanParallelism parallelism, IScanProgress? progress, CancellationToken ct, BranchPolicy? branches = null)
    {
        var results = await RepositoryWalker.ForEachAsync(repositories, parallelism, progress,
            async (repo, fileDegree, token) =>
            {
                var diagnostic = collectDiagnostics
                    ? new FrameworkRepositoryDiagnostic { Project = repo.Project, Repository = repo.Name }
                    : null;
                var scan = await ScanRepositoryAsync(api, repo, diagnostic, token, fileDegree, branches);
                return (Scan: scan, Diagnostic: diagnostic);
            }, ct);

        return new FrameworkScanResult(
            results.SelectMany(x => x.Scan.Frameworks).ToArray(),
            results.Select(x => x.Scan.Coverage).ToArray(),
            results.Where(x => x.Diagnostic is not null).Select(x => x.Diagnostic!).ToArray());
    }

    /// <summary>
    /// Scans one repository on its selected branch: one entry per framework of every project file found.
    /// Failures are reported in the coverage, never thrown, except cancellation.
    /// </summary>
    public static async Task<FrameworkRepositoryResult> ScanRepositoryAsync(IRepositoryProvider api,
        Repository repo, FrameworkRepositoryDiagnostic? diagnostic, CancellationToken ct, int fileParallelism = 1,
        BranchPolicy? branches = null)
    {
        var frameworks = new List<FrameworkOccurrence>();
        if (repo.IsDisabled)
            return new(new(repo.Project, repo.Name, null, CoverageStatus.Disabled, 0, 0, "Repository disabled"),
                frameworks);

        string? branchName = null;
        int filesRead = 0;
        var failures = new List<string>();
        try
        {
            var read = await RepositoryWalker.ReadAsync(api, repo, branches ?? BranchPolicy.RepositoryDefault,
                FrameworkParser.IsInterestingFile,
                FrameworkParser.Parse,
                (branches, selected) =>
                {
                    branchName = selected?.Name;
                    if (diagnostic is null) return;
                    diagnostic.AvailableBranches.AddRange(branches.Select(b => b.Name));
                    diagnostic.SelectedBranch = selected?.Name;
                    diagnostic.Commit = selected?.ObjectId;
                }, fileParallelism, ct);
            if (read.Branch is not { } branch)
                return new(new(repo.Project, repo.Name, null, CoverageStatus.NoBranch, 0, 0, read.NoBranchReason),
                    frameworks);

            // Unreadable files are kept as null, so that a project never falls back to a farther
            // Directory.Build.props than the one MSBuild would import. Git paths are case-sensitive:
            // two paths differing only by case are two projects.
            var files = new Dictionary<string, DeclaredFrameworks?>(StringComparer.Ordinal);
            foreach (var file in read.Files)
            {
                diagnostic?.Files.Add(new FrameworkFileDiagnostic(file.Path, file.ContentLength, file.Parsed,
                    file.Error));
                files[file.Path] = file.Parsed;
                if (file.Error is null) filesRead++;
                else failures.Add($"{file.Path}: {file.Error}");
            }

            // An unreadable project stays listed, as unresolved, and makes the repository partial.
            var projects = files.Keys.Where(NuGetDependencyParser.IsProjectFile).ToArray();
            foreach (var project in projects)
            foreach (var resolved in FrameworkParser.Resolve(project, files))
                frameworks.Add(new FrameworkOccurrence(repo.Project, repo.Name, branch.Name, branch.ObjectId,
                    project, resolved.Framework, resolved.Source, api.FileUrl(repo, branch.ObjectId, project)));

            var status = failures.Count > 0 ? CoverageStatus.Partial
                : projects.Length == 0 ? CoverageStatus.NoProjectFiles : CoverageStatus.OK;
            return new(new(repo.Project, repo.Name, branchName, status, filesRead, projects.Length,
                failures.Count > 0 ? string.Join(" | ", failures.Take(5)) : null), frameworks);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(new(repo.Project, repo.Name, branchName, CoverageStatus.Failed, filesRead, 0, ex.Message),
                frameworks);
        }
    }
}
