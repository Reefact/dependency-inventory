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
    public async Task<PackagePublication?> FindAsync(string id, NuGetVersion version, CancellationToken ct)
    {
        var versions = await _versions.GetOrAdd(id, _ => new(() => ListAsync(id, ct))).Value;
        return versions.FirstOrDefault(v => NuGetVersion.TryParse(v.Version, out var listed) && listed == version);
    }

    private async Task<IReadOnlyList<PackagePublication>> ListAsync(string id, CancellationToken ct) =>
        kind == PackageSource.Feed
            ? await feed.GetPublicationsAsync(id, ct)
            : (await feed.GetVersionsAsync(id, ct) ?? []).Select(v => new PackagePublication(v, null, null, [])).ToArray();

    public void Dispose() => feed.Dispose();
}

/// <summary>
/// Where a package version was found, and what it exposes. No content when it could not be read: the error says why.
/// <see cref="ReadFrom"/> says where the content came from: the local cache, the global packages folder or a source.
/// </summary>
public sealed record PackageResolution(
    PackageContent? Content,
    PackageSource? Source,
    string? Feed,
    string? ReadFrom,
    string? Error);

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
        return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "depinv", "package-content", "v1");
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

    public Task<PackageResolution> ResolveAsync(string id, NuGetVersion version, CancellationToken ct) =>
        _resolutions.GetOrAdd($"{id.ToLowerInvariant()}/{version.ToNormalizedString().ToLowerInvariant()}",
            _ => new(() => ReadAsync(id, version, ct))).Value;

    private async Task<PackageResolution> ReadAsync(string id, NuGetVersion version, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var source in _sources)
        {
            PackagePublication? found;
            try { found = await source.FindAsync(id, version, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var error = $"{source.Description}: {ex.Message}";
                _sourceFailures.TryAdd(error, 0);
                errors.Add(error);
                continue;
            }
            if (found is null) continue;

            // A public package saved in a feed from nuget.org is still a nuget.org package.
            var (kind, feed) = source.Kind == PackageSource.Feed && !found.FromPublicUpstream
                ? (PackageSource.Feed, source.FeedName)
                : (PackageSource.NuGetOrg, null);
            try
            {
                var (content, readFrom) = await ReadContentAsync(source, id, version, ct);
                return new PackageResolution(content, kind, feed, readFrom, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return new PackageResolution(null, kind, feed, source.Feed.Origin,
                    $"Cannot read the package from {source.Description}: {ex.Message}");
            }
        }
        var searched = string.Join(", ", _sources.Select(s => s.Description));
        return new PackageResolution(null, null, null, null, errors.Count > 0
            ? $"Not found in {searched} ({string.Join("; ", errors)})"
            : $"Not found in {searched}");
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
        var (lowerId, lowerVersion) = (id.ToLowerInvariant(), version.ToNormalizedString().ToLowerInvariant());
        // Two feeds may hold different packages under the same id and version: the cache is kept per source.
        var cacheFile = _cacheDirectory is null ? null
            : Path.Combine(_cacheDirectory, SourceKey(source.Feed.Origin), lowerId, lowerVersion + ".json");
        if (cacheFile is not null && File.Exists(cacheFile))
        {
            try
            {
                await using var stream = File.OpenRead(cacheFile);
                if (await JsonSerializer.DeserializeAsync<PackageContent>(stream, CacheFormat, ct) is { } cached)
                    return (cached, "Cache");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }

        PackageContent content;
        string readFrom;
        var extracted = _globalPackagesFolder is null ? null : Path.Combine(_globalPackagesFolder, lowerId, lowerVersion);
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

        // A published version never changes: its content is kept for the next runs. The cache is a convenience,
        // a failure to write it is ignored.
        if (cacheFile is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                var temporary = $"{cacheFile}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(content, CacheFormat), ct);
                File.Move(temporary, cacheFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return (content, readFrom);
    }
}
