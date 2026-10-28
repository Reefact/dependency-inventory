namespace DependencyInventory;

public sealed record FrameworkRepositoryResult(
    ProjectCoverage Coverage,
    IReadOnlyList<FrameworkOccurrence> Frameworks)
{
    /// <summary>The same scan, for another branch on the same commit.</summary>
    public FrameworkRepositoryResult OnBranch(string branch) => new(Coverage with { Branch = branch },
        Frameworks.Select(f => f with { Branch = branch }).ToArray());
}

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
        var perRepository = await RepositoryWalker.ForEachAsync(repositories, parallelism, progress,
            (repo, fileDegree, token) => ScanBranchesAsync(api, repo, collectDiagnostics, null, token, fileDegree,
                branches ?? BranchPolicy.RepositoryDefault), ct);
        var results = perRepository.SelectMany(x => x).ToArray();

        return new FrameworkScanResult(
            results.SelectMany(x => x.Scan.Frameworks).ToArray(),
            results.Select(x => x.Scan.Coverage).ToArray(),
            results.Where(x => x.Diagnostic is not null).Select(x => x.Diagnostic!).ToArray());
    }

    /// <summary>
    /// Scans one repository on the first branch <paramref name="branches"/> selects: one entry per framework of every
    /// project file found. Failures are reported in the coverage, never thrown, except cancellation.
    /// </summary>
    public static async Task<FrameworkRepositoryResult> ScanRepositoryAsync(IRepositoryProvider api,
        Repository repo, FrameworkRepositoryDiagnostic? diagnostic, CancellationToken ct, int fileParallelism = 1,
        BranchPolicy? branches = null) =>
        (await ScanBranchesAsync(api, repo, false, diagnostic, ct, fileParallelism,
            (branches ?? BranchPolicy.RepositoryDefault).With(BranchMode.First)))[0].Scan;

    /// <summary>
    /// Scans one repository on every branch <paramref name="branches"/> selects, in order: one result per branch,
    /// or a single one when no branch could be scanned. Each branch gets its own diagnostic, or
    /// <paramref name="diagnostic"/> for the first.
    /// </summary>
    internal static async Task<IReadOnlyList<(FrameworkRepositoryResult Scan, FrameworkRepositoryDiagnostic? Diagnostic)>>
        ScanBranchesAsync(IRepositoryProvider api, Repository repo, bool collectDiagnostics,
            FrameworkRepositoryDiagnostic? diagnostic, CancellationToken ct, int fileParallelism, BranchPolicy branches)
    {
        FrameworkRepositoryDiagnostic? NewDiagnostic()
        {
            var created = diagnostic ?? (collectDiagnostics
                ? new FrameworkRepositoryDiagnostic { Project = repo.Project, Repository = repo.Name }
                : null);
            diagnostic = null;
            return created;
        }

        if (repo.IsDisabled)
            return [(new(new(repo.Project, repo.Name, null, CoverageStatus.Disabled, 0, 0, "Repository disabled"), []),
                NewDiagnostic())];

        BranchSelection selection;
        try { selection = await RepositoryWalker.SelectBranchesAsync(api, repo, branches, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return [(new(new(repo.Project, repo.Name, null, CoverageStatus.Failed, 0, 0, ex.Message), []),
                NewDiagnostic())];
        }

        if (selection.Selected.Count == 0)
        {
            var none = NewDiagnostic();
            none?.AvailableBranches.AddRange(selection.Available.Select(b => b.Name));
            return [(new(new(repo.Project, repo.Name, null, CoverageStatus.NoBranch, 0, 0, selection.NoBranchReason),
                []), none)];
        }

        return await RepositoryWalker
            .ForEachBranchAsync<(FrameworkRepositoryResult Scan, FrameworkRepositoryDiagnostic? Diagnostic)>(api,
                selection,
                async (reader, branch) =>
                {
                    var branchDiagnostic = NewDiagnostic();
                    if (branchDiagnostic is not null)
                    {
                        branchDiagnostic.AvailableBranches.AddRange(selection.Available.Select(b => b.Name));
                        branchDiagnostic.SelectedBranch = branch.Name;
                        branchDiagnostic.Commit = branch.ObjectId;
                    }
                    return (await ScanCommitAsync(reader, repo, branch, branchDiagnostic, ct, fileParallelism),
                        branchDiagnostic);
                },
                (done, branch) => (done.Scan.OnBranch(branch.Name), done.Diagnostic?.OnBranch(branch.Name)));
    }

    /// <summary>
    /// Scans the commit of one branch. Failures are reported in the coverage, never thrown, except cancellation.
    /// </summary>
    private static async Task<FrameworkRepositoryResult> ScanCommitAsync(IRepositoryProvider api, Repository repo,
        GitBranch branch, FrameworkRepositoryDiagnostic? diagnostic, CancellationToken ct, int fileParallelism)
    {
        var frameworks = new List<FrameworkOccurrence>();
        var branchName = branch.Name;
        int filesRead = 0;
        var failures = new List<string>();
        try
        {
            var read = await RepositoryWalker.ReadAsync(api, repo, branch, FrameworkParser.IsInterestingFile,
                FrameworkParser.Parse, fileParallelism, ct);

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
