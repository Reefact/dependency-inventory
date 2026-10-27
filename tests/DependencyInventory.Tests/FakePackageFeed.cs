namespace DependencyInventory.Tests;

/// <summary>In-memory feed: packages, their versions, .nuspec and .nupkg files, and versions whose read fails.</summary>
internal sealed class FakePackageFeed : IPackageFeed
{
    private readonly Dictionary<string, Dictionary<string, (string? Nuspec, byte[]? Package)>> _packages =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);

    public string Origin => "https://pkgs.dev.azure.com/acme/_packaging/feed/nuget/v3/index.json";

    public FakePackageFeed Add(string id, string version, string? nuspec, byte[]? package = null)
    {
        if (!_packages.TryGetValue(id, out var versions))
            _packages[id] = versions = new Dictionary<string, (string?, byte[]?)>(StringComparer.OrdinalIgnoreCase);
        versions[version] = (nuspec, package);
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

    public Task<string?> GetNuspecAsync(string id, string version, CancellationToken ct) =>
        _failing.Contains($"{id}/{version}")
            ? throw new HttpRequestException($"cannot read {id} {version}")
            : Task.FromResult(Find(id, version)?.Nuspec);

    public Task<Stream?> GetPackageAsync(string id, string version, CancellationToken ct) =>
        Task.FromResult<Stream?>(Find(id, version)?.Package is { } bytes ? new MemoryStream(bytes) : null);

    private (string? Nuspec, byte[]? Package)? Find(string id, string version) =>
        _packages.TryGetValue(id, out var versions) && versions.TryGetValue(version, out var files) ? files : null;

    public void Dispose() { }
}
