namespace DependencyInventory.Tests;

/// <summary>In-memory feed: packages, their versions, .nuspec and .nupkg files, and versions whose read fails.</summary>
internal sealed class FakePackageFeed : IPackageFeed
{
    private readonly Dictionary<string, Dictionary<string, (string? Nuspec, byte[]? Package)>> _packages =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<PackagePublication>> _publications = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failingPublications = new(StringComparer.OrdinalIgnoreCase);

    private int _packageReads;

    public string Origin => "https://pkgs.dev.azure.com/acme/_packaging/feed/nuget/v3/index.json";

    /// <summary>Number of .nupkg files read.</summary>
    public int PackageReads => Volatile.Read(ref _packageReads);

    public FakePackageFeed Add(string id, string version, string? nuspec, byte[]? package = null)
    {
        if (!_packages.TryGetValue(id, out var versions))
            _packages[id] = versions = new Dictionary<string, (string?, byte[]?)>(StringComparer.OrdinalIgnoreCase);
        versions[version] = (nuspec, package);
        return this;
    }

    public FakePackageFeed Publish(string id, string version, DateTimeOffset published, bool listed, params string[] views)
    {
        if (!_publications.TryGetValue(id, out var publications)) _publications[id] = publications = [];
        publications.Add(new PackagePublication(version.ToLowerInvariant(), published, listed, views));
        return this;
    }

    /// <summary>A version the feed saved from a public upstream source such as nuget.org.</summary>
    public FakePackageFeed Cache(string id, string version)
    {
        if (!_publications.TryGetValue(id, out var publications)) _publications[id] = publications = [];
        publications.Add(new PackagePublication(version, null, true, [], FromPublicUpstream: true));
        return this;
    }

    public FakePackageFeed FailPublications(string id)
    {
        _failingPublications.Add(id);
        return this;
    }

    public FakePackageFeed Fail(string id, string version)
    {
        _failing.Add($"{id}/{version}");
        return this;
    }

    public Task<IReadOnlyList<string>> GetPackageIdsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(_packages.Keys.ToArray());

    public Task<IReadOnlyList<string>?> GetVersionsAsync(string id, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>?>(_packages.TryGetValue(id, out var versions)
            ? versions.Keys.Select(v => v.ToLowerInvariant()).ToArray() : null);

    public Task<IReadOnlyList<PackagePublication>> GetPublicationsAsync(string id, CancellationToken ct) =>
        _failingPublications.Contains(id)
            ? throw new HttpRequestException("feed API unavailable")
            : Task.FromResult<IReadOnlyList<PackagePublication>>(
                _publications.TryGetValue(id, out var publications) ? publications : []);

    public Task<string?> GetNuspecAsync(string id, string version, CancellationToken ct) =>
        _failing.Contains($"{id}/{version}")
            ? throw new HttpRequestException($"cannot read {id} {version}")
            : Task.FromResult(Find(id, version)?.Nuspec);

    public Task<Stream?> GetPackageAsync(string id, string version, CancellationToken ct)
    {
        Interlocked.Increment(ref _packageReads);
        return Task.FromResult<Stream?>(Find(id, version)?.Package is { } bytes ? new MemoryStream(bytes) : null);
    }

    private (string? Nuspec, byte[]? Package)? Find(string id, string version) =>
        _packages.TryGetValue(id, out var versions) && versions.TryGetValue(version, out var files) ? files : null;

    public void Dispose() { }
}
