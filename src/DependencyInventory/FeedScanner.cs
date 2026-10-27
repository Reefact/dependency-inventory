using NuGet.Versioning;

namespace DependencyInventory;

/// <summary>A package, and the one version to read; every published version when there is none.</summary>
public sealed record PackageRequest(string Id, string? Version)
{
    /// <summary>Parses --package: ID@VERSION or ID.</summary>
    public static PackageRequest Parse(string value)
    {
        var separator = value.LastIndexOf('@');
        var id = (separator < 0 ? value : value[..separator]).Trim();
        var version = separator < 0 ? null : value[(separator + 1)..].Trim();
        if (id.Length == 0 || id.Any(char.IsWhiteSpace))
            throw new ArgumentException($"Invalid --package '{value}': expected ID or ID@VERSION.");
        if (version is not null && !NuGetVersion.TryParse(version, out _))
            throw new ArgumentException($"Invalid --package '{value}': '{version}' is not a NuGet version.");
        return new PackageRequest(id, version);
    }
}

/// <summary>One package version to read, as requested: its id and version are written as given.</summary>
public sealed record PackageVersion(string Id, string Version);

/// <summary>What to read from the feed: package versions, whole packages, or packages matching filters.</summary>
public sealed record FeedRequest(
    IReadOnlyList<PackageRequest> Packages,
    DependencyMatcher? PackageFilter,
    bool Latest);

public sealed record FeedResolution(IReadOnlyList<PackageVersion> Versions, IReadOnlyList<PackageInfo> Unknown);

/// <summary>Everything read for one package version, written to diagnostics.json with --diagnostics.</summary>
public sealed record PackageDiagnostic(
    string Package,
    string Version,
    PackageStatus Status,
    FrameworksFrom? FrameworksFrom,
    IReadOnlyList<string> Frameworks,
    IReadOnlyList<NuspecDependency> Dependencies,
    string? Error);

public sealed record FeedScanResult(
    IReadOnlyList<PackageFramework> Frameworks,
    IReadOnlyList<PackageDependency> Dependencies,
    IReadOnlyList<PackageInfo> Packages,
    IReadOnlyList<PackageDiagnostic> Diagnostics)
{
    public int Failed => Packages.Count(x => x.Status == PackageStatus.Failed);
    public int NotFound => Packages.Count(x => x.Status == PackageStatus.NotFound);
}

/// <summary>Reads published package versions from a feed; no console access, so it can be tested directly.</summary>
public static class FeedScanner
{
    /// <summary>
    /// The package versions to read: the requested ones, every published version of the requested packages
    /// and of the feed's packages matching the filter (only the highest with <see cref="FeedRequest.Latest"/>).
    /// Ordered by package id, then version; a version requested twice is read once, as first requested.
    /// </summary>
    public static async Task<FeedResolution> ResolveAsync(IPackageFeed feed, FeedRequest request, CancellationToken ct)
    {
        // Requested versions come first, so that a version also found by a filter keeps the id and version
        // written by the user, which reports are joined on.
        var versions = request.Packages.Where(p => p.Version is not null)
            .Select(p => new PackageVersion(p.Id, p.Version!)).ToList();
        var unknown = new List<PackageInfo>();
        var wholePackages = request.Packages.Where(p => p.Version is null).Select(p => p.Id).ToList();
        if (request.PackageFilter is { } filter)
            wholePackages.AddRange((await feed.GetPackageIdsAsync(ct)).Where(id => filter.Match(id).Count > 0));

        foreach (var id in wholePackages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var published = await feed.GetVersionsAsync(id, ct);
            if (published is null)
            {
                unknown.Add(new PackageInfo(id, "", PackageStatus.NotFound, "Package not found in the feed"));
                continue;
            }
            var parsed = published
                .Select(v => NuGetVersion.TryParse(v, out var version) ? version : null)
                .Where(v => v is not null).Select(v => v!).ToArray();
            versions.AddRange((request.Latest ? parsed.OrderDescending().Take(1) : parsed)
                .Select(v => new PackageVersion(id, v.ToNormalizedString())));
        }

        var ordered = versions
            .DistinctBy(v => (v.Id.ToLowerInvariant(), NuGetVersion.Parse(v.Version)))
            .OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => NuGetVersion.Parse(v.Version))
            .ToArray();
        return new FeedResolution(ordered, unknown);
    }

    /// <summary>
    /// Reads every resolved version: its metadata, its publication, its frameworks and its dependencies kept by
    /// <paramref name="dependencyFilter"/> (all of them without filter).
    /// The packages list the unknown ones first, then the versions in the resolved order whatever the parallelism.
    /// A version that cannot be read is reported in the packages, never thrown, except cancellation.
    /// </summary>
    public static async Task<FeedScanResult> ReadAsync(IPackageFeed feed, FeedResolution resolution,
        DependencyMatcher? dependencyFilter, int parallelism, IProgress<PackageVersion>? progress,
        CancellationToken ct)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct };
        var versions = resolution.Versions;

        // The publications are listed once per package, for all its versions.
        var ids = versions.Select(v => v.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var publications = new (IReadOnlyList<PackagePublication> Versions, string? Error)[ids.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, ids.Length), options, async (index, token) =>
        {
            try { publications[index] = (await feed.GetPublicationsAsync(ids[index], token), null); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { publications[index] = ([], $"Publication unknown: {ex.Message}"); }
        });
        var publicationsById = ids.Zip(publications)
            .ToDictionary(x => x.First, x => x.Second, StringComparer.OrdinalIgnoreCase);

        var reads = new VersionRead[versions.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, versions.Count), options, async (index, token) =>
        {
            reads[index] = await ReadVersionAsync(feed, versions[index], token);
            progress?.Report(versions[index]);
        });

        var frameworks = new List<PackageFramework>();
        var dependencies = new List<PackageDependency>();
        var packages = new List<PackageInfo>(resolution.Unknown);
        for (var index = 0; index < versions.Count; index++)
        {
            var (id, version) = versions[index];
            var read = reads[index];
            frameworks.AddRange(read.Frameworks.Select(f => new PackageFramework(id, version, f)));
            dependencies.AddRange(read.Dependencies
                .Where(d => dependencyFilter is null || dependencyFilter.Match(d.Id).Count > 0)
                .Select(d => new PackageDependency(id, version, d.Framework ?? "", d.Id, d.VersionRange, d.Kind)));

            var (published, publicationError) = publicationsById[id];
            var publication = published.FirstOrDefault(p => NuGetVersion.TryParse(p.Version, out var v) &&
                v == NuGetVersion.Parse(version));
            packages.Add(new PackageInfo(id, version, read.Status,
                read.Error ?? (read.Status == PackageStatus.OK ? publicationError : null),
                read.FrameworksFrom, publication?.Published, publication?.Listed, publication?.Views, read.Metadata));
        }
        var diagnostics = versions.Zip(reads, (v, read) => new PackageDiagnostic(v.Id, v.Version, read.Status,
            read.FrameworksFrom, read.Frameworks, read.Dependencies, read.Error)).ToArray();
        return new FeedScanResult(frameworks, dependencies, packages, diagnostics);
    }

    private sealed record VersionRead(
        PackageStatus Status,
        string? Error,
        FrameworksFrom? FrameworksFrom = null,
        IReadOnlyList<string>? FrameworkList = null,
        IReadOnlyList<NuspecDependency>? DependencyList = null,
        PackageMetadata? Metadata = null)
    {
        public IReadOnlyList<string> Frameworks => FrameworkList ?? [];
        public IReadOnlyList<NuspecDependency> Dependencies => DependencyList ?? [];
    }

    private static async Task<VersionRead> ReadVersionAsync(IPackageFeed feed, PackageVersion version,
        CancellationToken ct)
    {
        try
        {
            var nuspec = await feed.GetNuspecAsync(version.Id, version.Version, ct);
            if (nuspec is null) return new VersionRead(PackageStatus.NotFound, "Version not found in the feed");

            var content = NuspecParser.Parse(nuspec);
            if (content.Frameworks.Count > 0)
                return new VersionRead(PackageStatus.OK, null, FrameworksFrom.Nuspec, content.Frameworks,
                    content.Dependencies, content.Metadata);

            // Without dependency groups, the .nuspec does not name the frameworks: the package folders do.
            await using var package = await feed.GetPackageAsync(version.Id, version.Version, ct)
                ?? throw new InvalidOperationException("The package file is missing from the feed.");
            return new VersionRead(PackageStatus.OK, null, FrameworksFrom.PackageFolders,
                NuspecParser.FrameworksFromPackage(package), content.Dependencies, content.Metadata);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new VersionRead(PackageStatus.Failed, ex.Message);
        }
    }
}

/// <summary>Reads the package versions of a dependencies.csv written by depinv packages.</summary>
public static class VersionsFile
{
    /// <summary>
    /// The Package and Version columns of every line. Versions that are not a literal NuGet version
    /// (empty, a range, an MSBuild property) are skipped and counted.
    /// </summary>
    /// <exception cref="FormatException">The file has no Package or Version column.</exception>
    public static async Task<(IReadOnlyList<PackageRequest> Requests, int Skipped)> ReadAsync(
        string path, CancellationToken ct)
    {
        var rows = ParseCsv(await File.ReadAllTextAsync(path, ct));
        var header = rows.FirstOrDefault() ?? [];
        var package = Array.FindIndex(header, c => c.Equals("Package", StringComparison.OrdinalIgnoreCase));
        var version = Array.FindIndex(header, c => c.Equals("Version", StringComparison.OrdinalIgnoreCase));
        if (package < 0 || version < 0)
            throw new FormatException($"{path} has no Package and Version columns: expected a dependencies.csv.");

        var requests = new List<PackageRequest>();
        var skipped = 0;
        foreach (var row in rows.Skip(1).Where(r => r.Length > Math.Max(package, version)))
        {
            var id = row[package].Trim();
            if (id.Length == 0) continue;
            if (NuGetVersion.TryParse(row[version].Trim(), out _)) requests.Add(new PackageRequest(id, row[version].Trim()));
            else skipped++;
        }
        return (requests, skipped);
    }

    /// <summary>Semicolon-separated values; a quoted value may hold ';', '"' (doubled) and line breaks.</summary>
    public static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var value = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') value.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ';') { row.Add(value.ToString()); value.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(value.ToString());
                value.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
                row.Clear();
            }
            else if (c != '\uFEFF') value.Append(c);
        }
        row.Add(value.ToString());
        if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
        return rows;
    }
}
