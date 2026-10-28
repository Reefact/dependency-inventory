using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NuGet.Versioning;

namespace DependencyInventory;

/// <summary>
/// One place the referenced packages are read from: an Azure Artifacts feed, or nuget.org. Owns the feed.
/// </summary>
public sealed class PackageSourceEntry(IPackageFeed feed, PackageSource kind, string? feedName) : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<PackagePublication>>>> _versions =
        new(StringComparer.OrdinalIgnoreCase);

    public IPackageFeed Feed => feed;
    public PackageSource Kind => kind;

    /// <summary>The feed name written in the reports; null for nuget.org.</summary>
    public string? FeedName => feedName;

    /// <summary>"feed my-feed" or "nuget.org", for messages.</summary>
    public string Description => kind == PackageSource.Feed ? $"feed {feedName}" : "nuget.org";

    /// <summary>
    /// The version as this source holds it, or null. A feed is asked for the versions it stores (published or
    /// saved from an upstream source), not those it could fetch from an upstream source: reading those would save
    /// them to the feed. Each package is asked once.
    /// </summary>
    public async Task<PackagePublication?> FindAsync(string id, NuGetVersion version, CancellationToken ct) =>
        (await ListAsync(id, ct))
            .FirstOrDefault(v => NuGetVersion.TryParse(v.Version, out var listed) && listed == version);

    /// <summary>The versions of a package this source holds; empty when it does not know the package.</summary>
    public Task<IReadOnlyList<PackagePublication>> ListAsync(string id, CancellationToken ct) =>
        _versions.GetOrAdd(id, _ => new(() => ReadVersionsAsync(id, ct))).Value;

    private async Task<IReadOnlyList<PackagePublication>> ReadVersionsAsync(string id, CancellationToken ct) =>
        kind == PackageSource.Feed
            ? await feed.GetPublicationsAsync(id, ct)
            : (await feed.GetVersionsAsync(id, ct) ?? []).Select(v => new PackagePublication(v, null, null, [])).ToArray();

    public void Dispose() => feed.Dispose();
}

/// <summary>
/// Which version of a package was read, where it was found, and what it exposes. No content when it could not be read:
/// the error says why. <see cref="ReadFrom"/> says where the content came from: the local cache, the global packages
/// folder or a source.
/// </summary>
public sealed record PackageResolution(
    string? Version,
    PackageContent? Content,
    PackageSource? Source,
    string? Feed,
    string? ReadFrom,
    string? Error);

/// <summary>
/// The dependencies a version declares in its .nuspec, where it was found, or why it could not be read.
/// </summary>
public sealed record PackageDependencies(
    string? Version,
    IReadOnlyList<NuspecDependency> Dependencies,
    PackageSource? Source,
    string? Feed,
    string? Error,
    IReadOnlyList<string>? DependencyGroups = null);

/// <summary>
/// The content of the referenced package versions, each resolved once per run: located in the sources in their order,
/// then read from the local cache, the global packages folder, or downloaded. Thread-safe.
/// </summary>
public sealed class PackageCatalog
{
    private static readonly JsonSerializerOptions CacheFormat = new() { WriteIndented = false };

    private readonly IReadOnlyList<PackageSourceEntry> _sources;
    private readonly string? _cacheDirectory;
    private readonly string? _globalPackagesFolder;
    private readonly ConcurrentDictionary<string, Lazy<Task<PackageResolution>>> _resolutions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<PackageDependencies>>> _dependencies =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _sourceFailures = new();

    public PackageCatalog(IReadOnlyList<PackageSourceEntry> sources, string? cacheDirectory = null,
        string? globalPackagesFolder = null)
    {
        _sources = sources;
        _cacheDirectory = cacheDirectory;
        _globalPackagesFolder = globalPackagesFolder;
    }

    /// <summary>The distinct errors returned by the sources, e.g. an access denied, for a warning.</summary>
    public IReadOnlyList<string> SourceFailures => _sourceFailures.Keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>The cache of the user's local data folder; null when the system has none.</summary>
    public static string? DefaultCacheDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "depinv", "package-content", "v2");
    }

    /// <summary>The NuGet global packages folder: NUGET_PACKAGES, otherwise ~/.nuget/packages; null when missing.</summary>
    public static string? DefaultGlobalPackagesFolder()
    {
        var folder = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(folder))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return null;
            folder = Path.Combine(home, ".nuget", "packages");
        }
        return Directory.Exists(folder) ? folder : null;
    }

    /// <summary>
    /// The version NuGet restores for a range: the lowest one the sources hold that satisfies it (the highest
    /// matching one for a floating range), prereleases only when the range's minimum is one. A declared version such
    /// as 1.2.3 is the range [1.2.3, ): when 1.2.3 is not published, NuGet restores the next one.
    /// </summary>
    public async Task<PackageResolution> ResolveAsync(string id, VersionRange range, CancellationToken ct)
    {
        var (version, error) = await SelectVersionAsync(id, range, ct);
        return version is null
            ? new PackageResolution(null, null, null, null, null, error)
            : await ResolveAsync(id, version, ct);
    }

    /// <summary>
    /// The dependencies of the version NuGet restores for a range, as its .nuspec declares them: read from the local
    /// cache, the global packages folder, or the .nuspec alone, never the whole package. Each version is read once.
    /// </summary>
    public async Task<PackageDependencies> ResolveDependenciesAsync(string id, VersionRange range, CancellationToken ct)
    {
        var (version, error) = await SelectVersionAsync(id, range, ct);
        return version is null
            ? new PackageDependencies(null, [], null, null, error)
            : await _dependencies.GetOrAdd(Key(id, version), _ => new(() => ReadDependenciesAsync(id, version, ct))).Value;
    }

    private async Task<(NuGetVersion? Version, string? Error)> SelectVersionAsync(string id, VersionRange range,
        CancellationToken ct)
    {
        var versions = new List<NuGetVersion>();
        var errors = new List<string>();
        foreach (var source in _sources)
        {
            try
            {
                versions.AddRange((await source.ListAsync(id, ct))
                    .Select(v => NuGetVersion.TryParse(v.Version, out var parsed) ? parsed : null)
                    .Where(v => v is not null).Select(v => v!));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add(Failure(source, ex)); }
        }
        var allowPrerelease = range.MinVersion?.IsPrerelease == true || range.Float?.IncludePrerelease == true;
        if (range.FindBestMatch(versions.Where(v => allowPrerelease || !v.IsPrerelease)) is { } best)
            return (best, null);

        var searched = string.Join(", ", _sources.Select(s => s.Description));
        var message = $"No version satisfying {range.PrettyPrint()} in {searched}";
        return (null, errors.Count > 0 ? $"{message} ({string.Join("; ", errors)})" : message);
    }

    private static string Key(string id, NuGetVersion version) =>
        $"{id.ToLowerInvariant()}/{version.ToNormalizedString().ToLowerInvariant()}";

    private string Failure(PackageSourceEntry source, Exception ex)
    {
        var error = $"{source.Description}: {ex.Message}";
        _sourceFailures.TryAdd(error, 0);
        return error;
    }

    public Task<PackageResolution> ResolveAsync(string id, NuGetVersion version, CancellationToken ct) =>
        _resolutions.GetOrAdd(Key(id, version), _ => new(() => ReadAsync(id, version, ct))).Value;

    private sealed record Location(PackageSourceEntry Source, PackageSource Kind, string? Feed);

    /// <summary>The first source holding the version, and the real origin of the package; or why none was found.</summary>
    private async Task<(Location? Location, string? Error)> LocateAsync(string id, NuGetVersion version,
        CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var source in _sources)
        {
            PackagePublication? found;
            try { found = await source.FindAsync(id, version, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                errors.Add(Failure(source, ex));
                continue;
            }
            if (found is null) continue;

            // A public package saved in a feed from nuget.org is still a nuget.org package.
            return source.Kind == PackageSource.Feed && !found.FromPublicUpstream
                ? (new Location(source, PackageSource.Feed, source.FeedName), null)
                : (new Location(source, PackageSource.NuGetOrg, null), null);
        }
        var searched = string.Join(", ", _sources.Select(s => s.Description));
        return (null, errors.Count > 0
            ? $"Not found in {searched} ({string.Join("; ", errors)})"
            : $"Not found in {searched}");
    }

    private async Task<PackageResolution> ReadAsync(string id, NuGetVersion version, CancellationToken ct)
    {
        var normalized = version.ToNormalizedString();
        var (location, error) = await LocateAsync(id, version, ct);
        if (location is null) return new PackageResolution(normalized, null, null, null, null, error);
        try
        {
            var (content, readFrom) = await ReadContentAsync(location.Source, id, version, ct);
            return new PackageResolution(normalized, content, location.Kind, location.Feed, readFrom, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new PackageResolution(normalized, null, location.Kind, location.Feed, location.Source.Feed.Origin,
                $"Cannot read the package from {location.Source.Description}: {ex.Message}");
        }
    }

    private async Task<PackageDependencies> ReadDependenciesAsync(string id, NuGetVersion version, CancellationToken ct)
    {
        var normalized = version.ToNormalizedString();
        var (location, error) = await LocateAsync(id, version, ct);
        if (location is null) return new PackageDependencies(normalized, [], null, null, error);
        try
        {
            var (dependencies, groups) = await ReadNuspecDependenciesAsync(location.Source, id, version, ct);
            return new PackageDependencies(normalized, dependencies, location.Kind, location.Feed, null, groups);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new PackageDependencies(normalized, [], location.Kind, location.Feed,
                $"Cannot read the .nuspec from {location.Source.Description}: {ex.Message}");
        }
    }

    /// <summary>
    /// The package dependencies a .nuspec declares: those of the content already cached when there is one, otherwise
    /// the cached .nuspec, the one of the global packages folder, or the one of the source, then cached.
    /// </summary>
    private async Task<(IReadOnlyList<NuspecDependency>, IReadOnlyList<string>?)> ReadNuspecDependenciesAsync(
        PackageSourceEntry source, string id, NuGetVersion version, CancellationToken ct)
    {
        if (CacheFile(source, id, version, ".json") is { } contentFile && File.Exists(contentFile) &&
            await ReadCachedContentAsync(contentFile, ct) is { } content)
            return (content.Dependencies, content.DependencyGroups);

        var nuspecFile = CacheFile(source, id, version, ".nuspec");
        string? nuspec = null;
        if (nuspecFile is not null && File.Exists(nuspecFile))
        {
            try { nuspec = await File.ReadAllTextAsync(nuspecFile, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var extracted = ExtractedFolder(id, version);
        if (nuspec is null && extracted is not null && await ExtractedFromAsync(extracted, source.Feed.Origin, ct) &&
            Directory.EnumerateFiles(extracted, "*.nuspec").FirstOrDefault() is { } extractedNuspec)
            nuspec = await File.ReadAllTextAsync(extractedNuspec, ct);
        if (nuspec is null)
        {
            nuspec = await source.Feed.GetNuspecAsync(id, version.ToNormalizedString(), ct)
                ?? throw new InvalidOperationException("The .nuspec file is missing.");
            if (nuspecFile is not null) await WriteCacheAsync(nuspecFile, nuspec, ct);
        }
        var parsed = NuspecParser.Parse(nuspec);
        return (parsed.Dependencies.Where(d => d.Kind == DependencyKind.Package).ToArray(), parsed.DependencyGroups);
    }

    /// <summary>The cache file of a version read from a source; null without cache.</summary>
    private string? CacheFile(PackageSourceEntry source, string id, NuGetVersion version, string extension) =>
        _cacheDirectory is null ? null
            // Two feeds may hold different packages under the same id and version: the cache is kept per source.
            : Path.Combine(_cacheDirectory, SourceKey(source.Feed.Origin), id.ToLowerInvariant(),
                version.ToNormalizedString().ToLowerInvariant() + extension);

    private string? ExtractedFolder(string id, NuGetVersion version) =>
        _globalPackagesFolder is null ? null
            : Path.Combine(_globalPackagesFolder, id.ToLowerInvariant(), version.ToNormalizedString().ToLowerInvariant());

    private static async Task<PackageContent?> ReadCachedContentAsync(string file, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<PackageContent>(stream, CacheFormat, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// A published version never changes: what was read of it is kept for the next runs. The cache is a convenience,
    /// a failure to write it is ignored.
    /// </summary>
    private static async Task WriteCacheAsync(string file, string text, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = $"{file}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporary, text, ct);
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A short name of a source for the cache folders: the beginning of the hash of its service index URL.</summary>
    private static string SourceKey(string origin) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(origin.TrimEnd('/').ToLowerInvariant())))[..16];

    /// <summary>
    /// True when NuGet extracted the package in this folder from <paramref name="origin"/>, as its .nupkg.metadata
    /// file says. NuGet writes that file last: without it, the extraction may be incomplete.
    /// </summary>
    private static async Task<bool> ExtractedFromAsync(string folder, string origin, CancellationToken ct)
    {
        var metadata = Path.Combine(folder, ".nupkg.metadata");
        if (!File.Exists(metadata)) return false;
        try
        {
            await using var stream = File.OpenRead(metadata);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return document.RootElement.TryGetProperty("source", out var source) &&
                string.Equals(source.GetString()?.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException) { return false; }
    }

    private async Task<(PackageContent Content, string ReadFrom)> ReadContentAsync(PackageSourceEntry source,
        string id, NuGetVersion version, CancellationToken ct)
    {
        var cacheFile = CacheFile(source, id, version, ".json");
        if (cacheFile is not null && File.Exists(cacheFile) && await ReadCachedContentAsync(cacheFile, ct) is { } cached)
            return (cached, "Cache");

        PackageContent content;
        string readFrom;
        var extracted = ExtractedFolder(id, version);
        if (extracted is not null && await ExtractedFromAsync(extracted, source.Feed.Origin, ct))
        {
            content = PackageContentReader.ReadFolder(extracted);
            readFrom = "GlobalPackagesFolder";
        }
        else
        {
            await using var package = await source.Feed.GetPackageAsync(id, version.ToNormalizedString(), ct)
                ?? throw new InvalidOperationException("The package file is missing.");
            content = PackageContentReader.ReadPackage(package);
            readFrom = source.Feed.Origin;
        }

        if (cacheFile is not null) await WriteCacheAsync(cacheFile, JsonSerializer.Serialize(content, CacheFormat), ct);
        return (content, readFrom);
    }
}
