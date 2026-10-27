using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NuGet.Versioning;

namespace DependencyInventory;

/// <summary>Read-only access to a NuGet feed: its packages, their versions and their contents.</summary>
public interface IPackageFeed : IDisposable
{
    /// <summary>The feed's service index URL.</summary>
    string Origin { get; }

    Task<IReadOnlyList<string>> GetPackageIdsAsync(CancellationToken ct);

    /// <summary>The published versions of a package; null when the feed does not know it.</summary>
    Task<IReadOnlyList<string>?> GetVersionsAsync(string id, CancellationToken ct);

    /// <summary>How the versions of a package are published; empty when the feed does not know it.</summary>
    Task<IReadOnlyList<PackagePublication>> GetPublicationsAsync(string id, CancellationToken ct);

    /// <summary>The .nuspec of a version; null when the version is not in the feed.</summary>
    Task<string?> GetNuspecAsync(string id, string version, CancellationToken ct);

    /// <summary>The .nupkg of a version; null when the version is not in the feed.</summary>
    Task<Stream?> GetPackageAsync(string id, string version, CancellationToken ct);
}

/// <summary>
/// An Azure Artifacts feed, as named in an Azure Pipelines task: "feed" for an organization feed,
/// "project/feed" for a project feed.
/// </summary>
public sealed record AzureFeed(string Organization, string? Project, string Name)
{
    private string Scope => Uri.EscapeDataString(Organization) +
        (Project is null ? "" : "/" + Uri.EscapeDataString(Project));

    public string IndexUrl => $"https://pkgs.dev.azure.com/{Scope}/_packaging/{Uri.EscapeDataString(Name)}/nuget/v3/index.json";

    public string PackagesApiUrl => $"https://feeds.dev.azure.com/{Scope}/_apis/packaging/Feeds/{Uri.EscapeDataString(Name)}/packages";

    public static AzureFeed Parse(AzureOrigin origin, string? feed)
    {
        var parts = (feed ?? "").Trim().Split('/');
        if (parts.Length is < 1 or > 2 || parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.Contains('\\')))
            throw new ArgumentException("--feed expects the feed name (organization feed) or project/feed (project feed).");
        return parts.Length == 1
            ? new AzureFeed(origin.Organization, null, parts[0])
            : new AzureFeed(origin.Organization, parts[0], parts[1]);
    }
}

/// <summary>An Azure Artifacts feed, read through its NuGet v3 API and the Azure DevOps feed API. GET only.</summary>
public sealed class AzureArtifactsFeed : IPackageFeed
{
    private const int PageSize = 1000;

    // The feed API returns release (isRelease defaults to true) and listed versions only, unless told otherwise.
    private static readonly string[] PublicationFilters =
        ["", "&isListed=false", "&isRelease=false", "&isRelease=false&isListed=false"];
    private readonly AzureFeed _feed;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private string? _packageBaseAddress;

    public string Origin => _feed.IndexUrl;

    public AzureArtifactsFeed(AzureFeed feed, string login, string pat, Action<RetryNotice>? onRetry = null)
        : this(feed, RepositoryHttpClient.Create(onRetry))
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{pat}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    /// <summary>Uses <paramref name="http"/> as is, authentication included.</summary>
    public AzureArtifactsFeed(AzureFeed feed, HttpClient http)
    {
        _feed = feed;
        _http = http;
    }

    public async Task<IReadOnlyList<string>> GetPackageIdsAsync(CancellationToken ct)
    {
        // The feed API leaves out packages whose versions are all prereleases or delisted, unless asked:
        // every combination of the two filters is listed and merged.
        var ids = new List<string>();
        foreach (var filters in PublicationFilters)
            ids.AddRange(await GetPackageIdsAsync(filters, ct));
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<string>> GetPackageIdsAsync(string filters, CancellationToken ct)
    {
        var ids = new List<string>();
        for (var skip = 0; ; skip += PageSize)
        {
            using var document = await ReadJsonAsync(
                $"{_feed.PackagesApiUrl}?protocolType=NuGet&%24top={PageSize}&%24skip={skip}{filters}&api-version=7.1", ct)
                ?? throw new HttpRequestException($"Feed not found: {_feed.IndexUrl}");
            var page = document.RootElement.GetProperty("value").EnumerateArray()
                .Select(p => p.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .ToArray();
            ids.AddRange(page);
            if (page.Length < PageSize) return ids;
        }
    }

    public async Task<IReadOnlyList<PackagePublication>> GetPublicationsAsync(string id, CancellationToken ct)
    {
        // With includeAllVersions, the feed API returns only release (isRelease defaults to true) and listed
        // versions. Every combination of the two filters is requested and merged, whatever their exact meaning.
        var publications = new List<PackagePublication>();
        foreach (var filters in PublicationFilters)
            publications.AddRange(await GetPublicationsAsync(id, filters, ct));
        return publications.DistinctBy(p => p.Version.ToLowerInvariant()).ToArray();
    }

    private async Task<IReadOnlyList<PackagePublication>> GetPublicationsAsync(string id, string filters,
        CancellationToken ct)
    {
        // packageNameQuery matches names containing the id: the package itself is the one named exactly so.
        using var document = await ReadJsonAsync($"{_feed.PackagesApiUrl}?protocolType=NuGet" +
            $"&packageNameQuery={Uri.EscapeDataString(id)}&includeAllVersions=true{filters}&api-version=7.1", ct);
        var package = document?.RootElement.GetProperty("value").EnumerateArray()
            .FirstOrDefault(p => p.TryGetProperty("name", out var name) &&
                string.Equals(name.GetString(), id, StringComparison.OrdinalIgnoreCase));
        if (package is not { ValueKind: JsonValueKind.Object } found ||
            !found.TryGetProperty("versions", out var versions))
            return [];

        return versions.EnumerateArray()
            .Select(v => new PackagePublication(
                v.TryGetProperty("normalizedVersion", out var normalized) ? normalized.GetString() ?? "" : "",
                v.TryGetProperty("publishDate", out var date) && date.TryGetDateTimeOffset(out var published)
                    ? published : null,
                v.TryGetProperty("isListed", out var listed) && listed.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? listed.GetBoolean() : null,
                v.TryGetProperty("views", out var views) && views.ValueKind == JsonValueKind.Array
                    ? views.EnumerateArray()
                        .Select(view => view.TryGetProperty("name", out var name) ? name.GetString() : null)
                        .Where(name => !string.IsNullOrEmpty(name)).Select(name => name!).ToArray()
                    : []))
            .Where(p => p.Version.Length > 0)
            .ToArray();
    }

    public async Task<IReadOnlyList<string>?> GetVersionsAsync(string id, CancellationToken ct)
    {
        using var document = await ReadJsonAsync($"{await PackageBaseAsync(ct)}{Lower(id)}/index.json", ct);
        return document?.RootElement.GetProperty("versions").EnumerateArray()
            .Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToArray();
    }

    public async Task<string?> GetNuspecAsync(string id, string version, CancellationToken ct)
    {
        using var response = await SendAsync(
            $"{await PackageBaseAsync(ct)}{Lower(id)}/{Normalized(version)}/{Lower(id)}.nuspec", ct);
        return response is null ? null : await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<Stream?> GetPackageAsync(string id, string version, CancellationToken ct)
    {
        using var response = await SendAsync(
            $"{await PackageBaseAsync(ct)}{Lower(id)}/{Normalized(version)}/{Lower(id)}.{Normalized(version)}.nupkg", ct);
        if (response is null) return null;
        var buffer = new MemoryStream();
        await response.Content.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return buffer;
    }

    /// <summary>The package content base address, read once from the service index.</summary>
    private async Task<string> PackageBaseAsync(CancellationToken ct)
    {
        if (_packageBaseAddress is { } known) return known;
        await _indexLock.WaitAsync(ct);
        try
        {
            if (_packageBaseAddress is not null) return _packageBaseAddress;
            using var index = await ReadJsonAsync(_feed.IndexUrl, ct)
                ?? throw new HttpRequestException($"Feed not found: {_feed.IndexUrl}");
            var address = index.RootElement.GetProperty("resources").EnumerateArray()
                .Where(r => r.TryGetProperty("@type", out var type) && type.GetString() == "PackageBaseAddress/3.0.0")
                .Select(r => r.GetProperty("@id").GetString())
                .FirstOrDefault() ?? throw new HttpRequestException("The feed has no package content resource.");
            return _packageBaseAddress = address.EndsWith('/') ? address : address + "/";
        }
        finally { _indexLock.Release(); }
    }

    // The NuGet package content API expects lowercase ids and versions.
    private static string Lower(string value) => Uri.EscapeDataString(value.ToLowerInvariant());

    // ...and normalized versions: 2.0 is 2.0.0, 1.0.0.0 is 1.0.0, build metadata is dropped.
    private static string Normalized(string version) =>
        Lower(NuGetVersion.TryParse(version, out var parsed) ? parsed.ToNormalizedString() : version);

    private async Task<JsonDocument?> ReadJsonAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        if (response is null) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>The response, or null on HTTP 404.</summary>
    private async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new HttpRequestException("Access denied: check the PAT (Packaging: Read) and the permissions on the feed.");
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Azure Artifacts returned HTTP {status}.");
        }
        return response;
    }

    public void Dispose()
    {
        _http.Dispose();
        _indexLock.Dispose();
    }
}
