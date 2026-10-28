using NuGet.Frameworks;
using NuGet.Versioning;

namespace DependencyInventory;

/// <summary>An MSBuild file of a repository: the packages it declares, the imports and frameworks it sets.</summary>
public sealed record MsBuildFile(
    IReadOnlyList<ParsedPackage> Packages,
    ProjectImports Imports,
    DeclaredFrameworks Frameworks)
{
    public static MsBuildFile Parse(string path, string xml) =>
        new(NuGetDependencyParser.Parse(path, xml), ProjectImports.Parse(xml), FrameworkParser.Parse(path, xml));
}

/// <summary>How the usage of one reference was decided, written to diagnostics.json with --diagnostics.</summary>
public sealed record UsageDiagnostic(
    string File,
    string Package,
    string? Version,
    string? ResolvedVersion,
    PackageUsage Usage,
    PackageSource? Source,
    string? Feed,
    string? ReadFrom,
    IReadOnlyList<string> Projects,
    int SourceFiles,
    string? Reason);

/// <summary>
/// Tells whether the code of a repository uses the packages it references, and which of their types, from the
/// syntax of its C# files: no compilation. No console access, so it can be tested directly.
/// </summary>
public sealed class PackageUsageAnalyzer(PackageCatalog catalog, bool listUsedTypes)
{
    // A package without types brings those of its dependencies (a metapackage such as xunit); followed this deep.
    private const int MaxDependencyDepth = 3;

    public bool ListUsedTypes => listUsedTypes;

    /// <summary>
    /// The occurrences with the usage of every reference (central versions are left as is), in the same order.
    /// Reads the C# files of the projects concerned from <paramref name="commit"/>. A failure of the analysis makes
    /// the usage undetermined, never the repository scan fail, except cancellation.
    /// </summary>
    public async Task<IReadOnlyList<PackageOccurrence>> AnalyzeAsync(IRepositoryProvider api, Repository repo,
        string commit, IReadOnlyList<string> tree, IReadOnlyDictionary<string, MsBuildFile> files,
        IReadOnlyList<PackageOccurrence> occurrences, RepositoryDiagnostic? diagnostic, int fileParallelism,
        CancellationToken ct)
    {
        var usages = diagnostic?.Usages.Count ?? 0;
        try
        {
            return await AnalyzeRepositoryAsync(api, repo, commit, tree, files, occurrences, diagnostic,
                fileParallelism, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            diagnostic?.Usages.RemoveRange(usages, diagnostic.Usages.Count - usages);
            var reason = $"Usage analysis failed: {ex.Message}";
            return occurrences.Select(o =>
            {
                if (o.Kind != PackageKind.Reference) return o;
                diagnostic?.Usages.Add(new UsageDiagnostic(o.File, o.Package, o.Version, null, PackageUsage.Undetermined,
                    null, null, null, [], 0, reason));
                return o with { Usage = PackageUsage.Undetermined, UsedTypes = listUsedTypes ? [] : null };
            }).ToArray();
        }
    }

    private async Task<IReadOnlyList<PackageOccurrence>> AnalyzeRepositoryAsync(IRepositoryProvider api,
        Repository repo, string commit, IReadOnlyList<string> tree, IReadOnlyDictionary<string, MsBuildFile> files,
        IReadOnlyList<PackageOccurrence> occurrences, RepositoryDiagnostic? diagnostic, int fileParallelism,
        CancellationToken ct)
    {
        var projectFiles = files.Keys.Where(NuGetDependencyParser.IsProjectFile).Order(StringComparer.Ordinal).ToArray();
        var targets = occurrences.Where(o => o.Kind == PackageKind.Reference).Select(o => o.File)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(file => file, file => TargetProjects(file, projectFiles), StringComparer.Ordinal);
        var projects = await ReadProjectsAsync(api, repo, commit, tree, files, projectFiles,
            targets.Values.SelectMany(t => t).Where(IsCSharp).ToHashSet(StringComparer.Ordinal), fileParallelism, ct);

        var analyzed = new List<PackageOccurrence>(occurrences.Count);
        foreach (var occurrence in occurrences)
        {
            if (occurrence.Kind != PackageKind.Reference)
            {
                analyzed.Add(occurrence);
                continue;
            }
            var (result, details) = await AnalyzeAsync(occurrence, targets[occurrence.File], projects, ct);
            analyzed.Add(occurrence with
            {
                Usage = result.Usage,
                Source = details.Source,
                Feed = details.Feed,
                UsedTypes = listUsedTypes ? result.Types : null
            });
            diagnostic?.Usages.Add(new UsageDiagnostic(occurrence.File, occurrence.Package, occurrence.Version,
                details.Version, result.Usage, details.Source, details.Feed, details.ReadFrom, targets[occurrence.File],
                targets[occurrence.File].Where(projects.ContainsKey).Sum(p => projects[p].Files.Count), result.Reason));
        }
        return analyzed;
    }

    private sealed record UsageResult(PackageUsage Usage, IReadOnlyList<UsedType> Types, string? Reason);

    private static UsageResult Undetermined(string reason) => new(PackageUsage.Undetermined, [], reason);

    private async Task<(UsageResult Result, PackageResolution Details)> AnalyzeAsync(PackageOccurrence occurrence,
        IReadOnlyList<string> targets, IReadOnlyDictionary<string, ProjectSources> projects, CancellationToken ct)
    {
        var none = new PackageResolution(null, null, null, null, null, null);
        if (ResolveRange(occurrence.Version) is not { } range)
            return (Undetermined(occurrence.Version is null
                ? "The version is not resolved."
                : $"The version '{occurrence.Version}' is neither a version nor a version range."), none);

        var resolution = await catalog.ResolveAsync(occurrence.Package, range, ct);
        if (resolution.Content is not { } content) return (Undetermined(resolution.Error ?? "Package not read."), resolution);
        if (targets.Count == 0) return (Undetermined("No project file in this file's folder."), resolution);

        var analyzedProjects = targets.Where(projects.ContainsKey).Select(p => projects[p]).ToArray();
        var surface = await SurfaceAsync(content, analyzedProjects.SelectMany(p => p.Frameworks).ToArray(), 0, ct);
        var used = analyzedProjects.SelectMany(surface.Index.UsedIn)
            .GroupBy(x => x.Type, StringComparer.Ordinal)
            .Select(g => new UsedType(g.Key, g.Sum(x => x.Files)))
            .OrderBy(t => t.Type, StringComparer.Ordinal)
            .ToArray();
        if (used.Length > 0) return (new UsageResult(PackageUsage.Used, used, null), resolution);

        var others = targets.Where(t => !IsCSharp(t)).ToArray();
        var unread = analyzedProjects.Sum(p => p.Failures);
        // A package acting on the build only is never reported unused, whatever the language of the projects.
        UsageResult result =
            content.DevelopmentDependency ? new(PackageUsage.BuildOnly, [], "Development dependency.")
            : !content.HasTypes && content.HasBuildAssets
                ? new(PackageUsage.BuildOnly, [], "No types of its own; build, analyzer, tool or content assets.")
            : others.Length > 0 ? Undetermined($"Not analyzed (C# only): {string.Join(", ", others)}.")
            : unread > 0 ? Undetermined($"{unread} source files could not be read.")
            : surface.Errors.Count > 0 ? Undetermined($"Dependency not read: {surface.Errors[0]}")
            : content.HasTypes ? new(PackageUsage.Unused, [], null)
            : surface.Index.IsEmpty ? Undetermined("The package has neither types nor build assets.")
            : new(PackageUsage.Unused, [], "No types of its own; those of its dependencies are not used.");
        return (result, resolution);
    }

    /// <summary>
    /// The range a declared version stands for, as NuGet reads it: 1.2.3 is [1.2.3, ), and ranges and floating versions
    /// (1.*) are kept. Null for anything else, such as an MSBuild property.
    /// </summary>
    public static VersionRange? ResolveRange(string? version) =>
        !string.IsNullOrWhiteSpace(version) && VersionRange.TryParse(version.Trim(), allowFloating: true, out var range)
            ? range : null;

    private sealed record Surface(TypeIndex Index, IReadOnlyList<string> Errors);

    /// <summary>
    /// The types a package brings to the projects: its own, or, when it has none, those of its dependencies for the
    /// given frameworks (all of them when no framework is known), at their minimum version.
    /// </summary>
    private async Task<Surface> SurfaceAsync(PackageContent content, IReadOnlyList<string> frameworks, int depth,
        CancellationToken ct)
    {
        if (content.HasTypes || depth >= MaxDependencyDepth) return new(new TypeIndex(content), []);

        var types = new List<PackageType>();
        var extensions = new List<ExtensionMethod>();
        var errors = new List<string>();
        foreach (var dependency in Dependencies(content, frameworks))
        {
            if (dependency.VersionRange is null || ResolveRange(dependency.VersionRange) is not { } range) continue;
            var resolution = await catalog.ResolveAsync(dependency.Id, range, ct);
            if (resolution.Content is not { } dependencyContent)
            {
                errors.Add($"{dependency.Id} {range.PrettyPrint()}: {resolution.Error}");
                continue;
            }
            var surface = await SurfaceAsync(dependencyContent, frameworks, depth + 1, ct);
            types.AddRange(surface.Index.Types);
            extensions.AddRange(surface.Index.Extensions);
            errors.AddRange(surface.Errors);
        }
        return new(new TypeIndex(new PackageContent(types, extensions, false, false, [])), errors);
    }

    /// <summary>The dependencies of the group nearest to each framework; every group when no framework is known.</summary>
    private static IEnumerable<NuspecDependency> Dependencies(PackageContent content, IReadOnlyList<string> frameworks)
    {
        var groups = content.Dependencies.GroupBy(d => d.Framework ?? "", StringComparer.OrdinalIgnoreCase).ToArray();
        var parsed = frameworks.Select(NuGetFramework.Parse).Where(f => !f.IsUnsupported).ToArray();
        // Frameworks may depend on different versions of a package: each version is kept.
        if (parsed.Length == 0) return content.Dependencies.DistinctBy(DependencyKey);
        return parsed
            .SelectMany(framework => NuGetFrameworkUtility.GetNearest(groups, framework,
                g => g.Key.Length == 0 ? NuGetFramework.AnyFramework : NuGetFramework.Parse(g.Key))
                ?? Enumerable.Empty<NuspecDependency>())
            .DistinctBy(DependencyKey);
    }

    private static (string, string?) DependencyKey(NuspecDependency dependency) =>
        (dependency.Id.ToLowerInvariant(), ResolveRange(dependency.VersionRange)?.ToNormalizedString());

    // --- Projects and their source files

    private sealed record ProjectSources(
        IReadOnlyList<SourceSymbols> Files,
        int Failures,
        IReadOnlySet<string> Imports,
        IReadOnlySet<string> QualifiedUsings,
        IReadOnlyList<string> Frameworks);

    private static bool IsCSharp(string projectFile) => projectFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static string Folder(string path) => path[..(path.LastIndexOf('/') + 1)];

    /// <summary>
    /// The projects a reference applies to: the project file itself, otherwise (Directory.Build.props, packages.config,
    /// any imported file) every project under its folder.
    /// </summary>
    private static IReadOnlyList<string> TargetProjects(string file, IReadOnlyList<string> projectFiles) =>
        NuGetDependencyParser.IsProjectFile(file)
            ? [file]
            : projectFiles.Where(p => p.StartsWith(Folder(file), StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// Reads and parses the C# files of the given projects. A file belongs to the project of the nearest folder above
    /// it; projects of the same folder share their files.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, ProjectSources>> ReadProjectsAsync(IRepositoryProvider api,
        Repository repo, string commit, IReadOnlyList<string> tree, IReadOnlyDictionary<string, MsBuildFile> files,
        IReadOnlyList<string> projectFiles, IReadOnlySet<string> projects, int fileParallelism, CancellationToken ct)
    {
        var projectFolders = projectFiles.Select(Folder).Distinct(StringComparer.Ordinal).ToArray();
        var wantedFolders = projects.Select(Folder).ToHashSet(StringComparer.Ordinal);
        var sources = tree.Where(SourceCodeParser.IsSourceFile)
            .Select(path => (Path: path, Folder: projectFolders.Where(f => path.StartsWith(f, StringComparison.Ordinal))
                .MaxBy(f => f.Length)))
            .Where(x => x.Folder is not null && wantedFolders.Contains(x.Folder))
            .ToArray();
        var reads = await RepositoryWalker.ReadFilesAsync(api, repo, commit, sources.Select(x => x.Path).ToArray(),
            SourceCodeParser.Parse, fileParallelism, ct);
        var readsByFolder = sources.Zip(reads).ToLookup(x => x.First.Folder!, x => x.Second, StringComparer.Ordinal);

        var buildProps = files.Where(f => f.Key.EndsWith("/Directory.Build.props", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var declared = files.ToDictionary(f => f.Key, f => (DeclaredFrameworks?)f.Value.Frameworks, StringComparer.Ordinal);
        var result = new Dictionary<string, ProjectSources>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var folderReads = readsByFolder[Folder(project)].ToArray();
            var parsed = folderReads.Where(r => r.Error is null).Select(r => r.Parsed!).ToArray();
            // Only the nearest Directory.Build.props is imported automatically.
            var props = buildProps.Where(p => Folder(project).StartsWith(Folder(p.Key), StringComparison.Ordinal))
                .OrderByDescending(p => p.Key.Length).Select(p => p.Value).FirstOrDefault();
            var imports = files[project].Imports.With(props?.Imports);
            result[project] = new ProjectSources(parsed, folderReads.Length - parsed.Length,
                imports.Namespaces.Concat(parsed.SelectMany(f => f.GlobalImports)).ToHashSet(StringComparer.Ordinal),
                imports.QualifiedUsings.ToHashSet(StringComparer.Ordinal),
                FrameworkParser.Resolve(project, declared).Where(f => f.Framework is not null)
                    .Select(f => f.Framework!).ToArray());
        }
        return result;
    }

    // --- Matching types and code

    /// <summary>The types and extension methods of a package, indexed by simple name.</summary>
    private sealed class TypeIndex
    {
        private readonly ILookup<string, PackageType> _byName;
        private readonly ILookup<string, ExtensionMethod> _extensionsByName;

        public IReadOnlyList<PackageType> Types { get; }
        public IReadOnlyList<ExtensionMethod> Extensions { get; }
        public bool IsEmpty => Types.Count == 0 && Extensions.Count == 0;

        public TypeIndex(PackageContent content)
        {
            Types = content.Types;
            Extensions = content.ExtensionMethods;
            _byName = Types.ToLookup(t => t.Name, StringComparer.Ordinal);
            _extensionsByName = Extensions.ToLookup(e => e.Name, StringComparer.Ordinal);
        }

        /// <summary>
        /// The types of the package referenced by a project, with the number of its files referencing them. A type
        /// named by a Using item of the project file (Static or Alias) counts as used, without file.
        /// </summary>
        public IEnumerable<(string Type, int Files)> UsedIn(ProjectSources project)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var type in project.QualifiedUsings.SelectMany(q => Qualified(q, project.Imports.Contains)))
                counts.TryAdd(type, 0);
            foreach (var file in project.Files)
            {
                bool Imported(string ns) => ns.Length == 0 || file.Imports.Contains(ns) || project.Imports.Contains(ns);
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var name in file.Names)
                foreach (var type in _byName[name])
                    if (Imported(type.Namespace)) used.Add(type.FullName);
                foreach (var name in file.QualifiedNames.Concat(file.GlobalQualifiedNames))
                    used.UnionWith(Qualified(name, Imported));
                foreach (var member in file.InvokedMembers)
                foreach (var extension in _extensionsByName[member])
                    if (Imported(extension.Namespace)) used.Add(extension.DeclaringType);
                foreach (var type in used) counts[type] = counts.GetValueOrDefault(type) + 1;
            }
            return counts.Select(x => (x.Key, x.Value));
        }

        /// <summary>The types a dotted name designates: fully qualified, or relative to an imported namespace.</summary>
        private IEnumerable<string> Qualified(string name, Func<string, bool> imported)
        {
            var simple = name[(name.LastIndexOf('.') + 1)..];
            foreach (var type in _byName[simple])
            {
                var full = type.FullName;
                if (full == name ||
                    full.EndsWith("." + name, StringComparison.Ordinal) && imported(full[..^(name.Length + 1)]))
                    yield return full;
            }
        }
    }
}
