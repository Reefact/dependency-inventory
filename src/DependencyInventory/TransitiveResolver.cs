using System.Text.Json;
using NuGet.Versioning;

namespace DependencyInventory;

/// <summary>How the transitive dependencies of one project were found, written to diagnostics.json with --diagnostics.</summary>
public sealed record TransitiveDiagnostic(
    string File,
    string? Framework,
    DependencyResolution Resolution,
    int Packages,
    IReadOnlyList<string> Unresolved);

/// <summary>
/// The packages each project of a repository depends on without declaring them: read from its packages.lock.json when
/// the repository has one, otherwise computed from the .nuspec of the packages, following the NuGet rules, without
/// restore. No console access, so it can be tested directly.
/// </summary>
public sealed class TransitiveResolver(PackageCatalog catalog)
{
    private const string LockFileName = "packages.lock.json";

    // A graph deeper than this is cut: real ones stay far below, and a cycle stops earlier anyway.
    private const int MaxDepth = 32;

    /// <summary>
    /// The transitive dependencies matching the filters, per project and target framework. Every declared reference
    /// takes part in the graph, matching the filters or not. A failure makes the project's graph empty, with its
    /// reason in the diagnostics; it never fails the repository scan, except cancellation.
    /// </summary>
    public async Task<IReadOnlyList<TransitiveDependency>> ResolveAsync(IRepositoryProvider api, Repository repo,
        GitBranch branch, IReadOnlyList<string> tree, IReadOnlyDictionary<string, MsBuildFile> files,
        DependencyMatcher matcher, RepositoryDiagnostic? diagnostic, int fileParallelism, CancellationToken ct)
    {
        var projectFiles = files.Keys.Where(NuGetDependencyParser.IsProjectFile).Order(StringComparer.Ordinal).ToArray();
        var treeFiles = tree.ToHashSet(StringComparer.Ordinal);
        var lockPaths = projectFiles.Select(p => PackageUsageAnalyzer.Folder(p) + LockFileName)
            .Where(treeFiles.Contains).Distinct(StringComparer.Ordinal).ToArray();
        var lockFiles = (await RepositoryWalker.ReadFilesAsync(api, repo, branch.ObjectId, lockPaths, LockFile.Parse,
            fileParallelism, ct)).ToDictionary(f => f.Path, StringComparer.Ordinal);

        var rows = new List<TransitiveDependency>();
        foreach (var project in projectFiles)
        {
            IReadOnlyList<Graph> graphs;
            try
            {
                var lockRead = lockFiles.GetValueOrDefault(PackageUsageAnalyzer.Folder(project) + LockFileName);
                graphs = lockRead is { Parsed: { } lockFile }
                    ? lockFile.Graphs()
                    : await ComputeAsync(project, files, lockRead?.Error is { } error
                        ? $"{LockFileName} not read, the graph is computed: {error}" : null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                graphs = [new Graph(null, DependencyResolution.Computed, [], [$"Transitive resolution failed: {ex.Message}"])];
            }

            foreach (var graph in graphs)
            {
                diagnostic?.Transitive.Add(new TransitiveDiagnostic(project, graph.Framework, graph.Resolution,
                    graph.Packages.Count, graph.Unresolved));
                foreach (var package in graph.Packages)
                {
                    var matched = matcher.Match(package.Id);
                    if (matched.Count == 0) continue;
                    rows.Add(new TransitiveDependency(repo.Project, repo.Name, branch.Name, branch.ObjectId, project,
                        graph.Framework, package.Id, package.Version, package.Via, graph.Resolution, matched));
                }
            }
        }
        return rows;
    }

    /// <summary>A package reached transitively, at its resolved version.</summary>
    internal sealed record Reached(string Id, string Version, string Via);

    /// <summary>The transitive packages of a project for one framework, and the branches that could not be followed.</summary>
    internal sealed record Graph(
        string? Framework,
        DependencyResolution Resolution,
        IReadOnlyList<Reached> Packages,
        IReadOnlyList<string> Unresolved);

    // --- Computed graph

    private sealed record Node(string Id, PackageDependencies Package, string Via);

    /// <summary>
    /// The graph of a project, per target framework (once, without framework, when none is known): its declared
    /// references, those of the project file and of the MSBuild files above it, then their dependencies level by level.
    /// </summary>
    private async Task<IReadOnlyList<Graph>> ComputeAsync(string project, IReadOnlyDictionary<string, MsBuildFile> files,
        string? note, CancellationToken ct)
    {
        var folder = PackageUsageAnalyzer.Folder(project);
        var declaring = files.Where(f => f.Key == project ||
                !NuGetDependencyParser.IsProjectFile(f.Key) &&
                folder.StartsWith(PackageUsageAnalyzer.Folder(f.Key), StringComparison.Ordinal))
            .ToArray();
        var packages = files.ToDictionary(f => f.Key, f => f.Value.Packages, StringComparer.Ordinal);
        var direct = new List<(string Id, VersionRange? Range, string? Declared)>();
        foreach (var (file, content) in declaring)
        foreach (var package in content.Packages.Where(p => p.Kind == PackageKind.Reference))
        {
            var version = package.Version ??
                NuGetDependencyParser.ResolveCentralVersion(project, package.Id, packages);
            direct.Add((package.Id, PackageUsageAnalyzer.ResolveRange(version), version));
        }

        // Central Package Management: with transitive pinning, a central version applies to transitive packages too.
        var central = Nearest(files, project, "/Directory.Packages.props");
        var pinning = files[project].TransitivePinning || central?.Value.TransitivePinning == true ||
            Nearest(files, project, "/Directory.Build.props")?.Value.TransitivePinning == true;
        var pinned = pinning && central is { } props
            ? props.Value.Packages.Where(p => p.Kind == PackageKind.CentralVersion && p.Version is not null)
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Version!, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var frameworks = FrameworkParser.Resolve(project,
                files.ToDictionary(f => f.Key, f => (DeclaredFrameworks?)f.Value.Frameworks, StringComparer.Ordinal))
            .Select(f => f.Framework).Where(f => f is not null).Select(f => f!).ToArray();
        var graphs = new List<Graph>();
        foreach (var framework in frameworks.Length > 0 ? frameworks.Select(f => (string?)f) : [null])
            graphs.Add(await ComputeAsync(framework, direct, pinned, note, ct));
        return graphs;
    }

    private static KeyValuePair<string, MsBuildFile>? Nearest(IReadOnlyDictionary<string, MsBuildFile> files,
        string project, string name) =>
        files.Where(f => f.Key.EndsWith(name, StringComparison.OrdinalIgnoreCase) &&
                PackageUsageAnalyzer.Folder(project).StartsWith(PackageUsageAnalyzer.Folder(f.Key), StringComparison.Ordinal))
            .OrderByDescending(f => f.Key.Length)
            .Select(f => (KeyValuePair<string, MsBuildFile>?)f)
            .FirstOrDefault();

    /// <summary>
    /// NuGet's resolution, level by level: a declared reference always wins; otherwise the nearest level wins, and
    /// among the ranges of one level, the one with the highest minimum. Each range gives the lowest published version
    /// satisfying it. A package already resolved is not followed again, which also stops cycles.
    /// </summary>
    private async Task<Graph> ComputeAsync(string? framework,
        IReadOnlyList<(string Id, VersionRange? Range, string? Declared)> direct,
        IReadOnlyDictionary<string, string> pinned, string? note, CancellationToken ct)
    {
        var frameworks = framework is null ? Array.Empty<string>() : [framework];
        var unresolved = new List<string>();
        if (note is not null) unresolved.Add(note);
        var seen = new HashSet<string>(direct.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
        var reached = new List<Reached>();
        var frontier = new List<Node>();
        foreach (var (id, range, declared) in direct.DistinctBy(d => d.Id.ToLowerInvariant()))
        {
            if (range is null)
            {
                unresolved.Add($"{id}: the declared version '{declared}' is neither a version nor a version range.");
                continue;
            }
            var resolution = await catalog.ResolveDependenciesAsync(id, range, ct);
            if (resolution.Version is null || resolution.Error is not null)
                unresolved.Add($"{id} {range.PrettyPrint()}: {resolution.Error}");
            if (resolution.Version is not null) frontier.Add(new Node(id, resolution, id));
        }

        for (var depth = 1; depth <= MaxDepth && frontier.Count > 0; depth++)
        {
            var candidates = new Dictionary<string, (string Id, List<VersionRange> Ranges, string Via)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var node in frontier.OrderBy(n => n.Via, StringComparer.Ordinal))
            foreach (var dependency in PackageUsageAnalyzer.Dependencies(node.Package.Dependencies,
                         node.Package.DependencyGroups, frameworks))
            {
                if (seen.Contains(dependency.Id)) continue;
                var range = dependency.VersionRange is null ? VersionRange.All
                    : PackageUsageAnalyzer.ResolveRange(dependency.VersionRange) ?? VersionRange.All;
                if (!candidates.TryGetValue(dependency.Id, out var candidate))
                    candidates[dependency.Id] = candidate = (dependency.Id, [], $"{node.Via} > {dependency.Id}");
                candidate.Ranges.Add(range);
            }

            var next = new List<Node>();
            foreach (var (id, ranges, via) in candidates.Values.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(id);
                var range = pinned.TryGetValue(id, out var version) && PackageUsageAnalyzer.ResolveRange(version) is { } pin
                    ? pin
                    : ranges.MaxBy(r => r.MinVersion ?? new NuGetVersion(0, 0, 0))!;
                var resolution = await catalog.ResolveDependenciesAsync(id, range, ct);
                if (resolution.Version is null)
                {
                    unresolved.Add($"{via} {range.PrettyPrint()}: {resolution.Error}");
                    continue;
                }
                reached.Add(new Reached(id, resolution.Version, via));
                if (resolution.Error is not null) unresolved.Add($"{via} {resolution.Version}: {resolution.Error}");
                next.Add(new Node(id, resolution, via));
            }
            frontier = next;
        }
        if (frontier.Count > 0) unresolved.Add($"The graph is cut at depth {MaxDepth}.");
        return new Graph(framework, DependencyResolution.Computed,
            reached.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToArray(), unresolved);
    }

    // --- Lock file

    /// <summary>
    /// A packages.lock.json: per target framework, the packages NuGet resolved, each with its type (Direct,
    /// Transitive, CentralTransitive, Project) and its dependencies. Runtime-specific sections are left out.
    /// </summary>
    internal sealed record LockFile(IReadOnlyList<LockTarget> Targets)
    {
        public static LockFile Parse(string path, string json)
        {
            using var document = JsonDocument.Parse(json);
            var targets = new List<LockTarget>();
            foreach (var target in document.RootElement.GetProperty("dependencies").EnumerateObject())
            {
                if (target.Name.Contains('/')) continue;
                var entries = target.Value.EnumerateObject().Select(entry => new LockEntry(entry.Name,
                        entry.Value.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
                        entry.Value.TryGetProperty("resolved", out var resolved) ? resolved.GetString() : null,
                        entry.Value.TryGetProperty("dependencies", out var dependencies) &&
                        dependencies.ValueKind == JsonValueKind.Object
                            ? dependencies.EnumerateObject().Select(d => d.Name).ToArray()
                            : []))
                    .ToArray();
                targets.Add(new LockTarget(NuspecParser.Normalize(target.Name) ?? target.Name, entries));
            }
            return new LockFile(targets);
        }

        /// <summary>
        /// The transitive packages of every framework, each with the shortest path from a declared reference or a
        /// referenced project.
        /// </summary>
        public IReadOnlyList<Graph> Graphs() => Targets.Select(target =>
        {
            var byId = target.Entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
            var via = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            foreach (var root in target.Entries.Where(e => e.Type is "Direct" or "Project"))
            {
                via[root.Id] = root.Id;
                queue.Enqueue(root.Id);
            }
            while (queue.TryDequeue(out var id))
                foreach (var dependency in byId.TryGetValue(id, out var entry) ? entry.Dependencies : [])
                    if (via.TryAdd(dependency, $"{via[id]} > {byId.GetValueOrDefault(dependency)?.Id ?? dependency}"))
                        queue.Enqueue(dependency);

            var packages = target.Entries
                .Where(e => e.Type is "Transitive" or "CentralTransitive" && e.Resolved is not null)
                .Select(e => new Reached(e.Id, e.Resolved!, via.GetValueOrDefault(e.Id) ?? e.Id))
                .OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new Graph(target.Framework, DependencyResolution.LockFile, packages, []);
        }).ToArray();
    }

    internal sealed record LockTarget(string Framework, IReadOnlyList<LockEntry> Entries);

    internal sealed record LockEntry(string Id, string Type, string? Resolved, IReadOnlyList<string> Dependencies);
}
