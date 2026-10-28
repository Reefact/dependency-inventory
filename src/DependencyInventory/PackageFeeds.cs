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

    /// <summary>The .nupkg of a version, as a seekable stream to dispose; null when the version is not in the feed.</summary>
    Task<Stream?> GetPackageAsync(string id, string version, CancellationToken ct);
}

/// <summary>
/// An Azure Artifacts feed, as named in an Azure Pipelines task: "feed" for an organization feed,
/// "project/feed" for a project feed.
/// </summary>
public sealed record AzureFeed(string Organization, string? Project, string Name)
{
    private const string UrlFormat = "https://dev.azure.com/organization[/project]/_packaging/feed";

    private string Scope => Uri.EscapeDataString(Organization) +
        (Project is null ? "" : "/" + Uri.EscapeDataString(Project));

    /// <summary>The name written in the reports: "feed" or "project/feed".</summary>
    public string DisplayName => Project is null ? Name : $"{Project}/{Name}";

    public string IndexUrl => $"https://pkgs.dev.azure.com/{Scope}/_packaging/{Uri.EscapeDataString(Name)}/nuget/v3/index.json";

    public string PackagesApiUrl => $"https://feeds.dev.azure.com/{Scope}/_apis/packaging/Feeds/{Uri.EscapeDataString(Name)}/packages";

    public string FeedApiUrl => $"https://feeds.dev.azure.com/{Scope}/_apis/packaging/Feeds/{Uri.EscapeDataString(Name)}";

    /// <summary>
    /// Parses --feed: a URL (https://dev.azure.com/organization[/project]/_packaging/feed, or the feed's
    /// pkgs.dev.azure.com service index), otherwise a name of the organization of <paramref name="origin"/>:
    /// "feed" or "project/feed". Without origin, only a URL is accepted.
    /// </summary>
    public static AzureFeed Parse(AzureOrigin? origin, string? feed)
    {
        var value = (feed ?? "").Trim();
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return ParseUrl(value);
        if (origin is null)
            throw new ArgumentException(
                $"--feed '{value}': give the feed URL ({UrlFormat}) when the code is not on Azure DevOps.");

        var parts = value.Split('/');
        if (parts.Length is < 1 or > 2 || parts.Any(InvalidSegment))
            throw new ArgumentException(
                $"--feed expects the feed name (organization feed), project/feed (project feed) or {UrlFormat}.");
        return parts.Length == 1
            ? new AzureFeed(origin.Organization, null, parts[0])
            : new AzureFeed(origin.Organization, parts[0], parts[1]);
    }

    private static AzureFeed ParseUrl(string value)
    {
        var invalid = new ArgumentException($"Invalid feed URL '{value}': expected {UrlFormat}.");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !(uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("pkgs.dev.azure.com", StringComparison.OrdinalIgnoreCase)))
            throw invalid;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        var packaging = Array.FindIndex(parts, p => p.Equals("_packaging", StringComparison.OrdinalIgnoreCase));
        // Only the service index may follow the feed name: .../_packaging/feed/nuget/v3/index.json.
        var rest = packaging < 0 ? [] : parts.Skip(packaging + 2).ToArray();
        if (packaging is not (1 or 2) || parts.Length < packaging + 2 || parts.Any(InvalidSegment) ||
            !(rest.Length == 0 || string.Join('/', rest).Equals("nuget/v3/index.json", StringComparison.OrdinalIgnoreCase)))
            throw invalid;
        return new AzureFeed(parts[0], packaging == 2 ? parts[1] : null, parts[packaging + 1]);
    }

    private static bool InvalidSegment(string part) =>
        string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.Contains('\\');
}

/// <summary>An Azure Artifacts feed, read through its NuGet v3 API and the Azure DevOps feed API. GET only.</summary>
public sealed class AzureArtifactsFeed : IPackageFeed
{
    public const int PageSize = 1000;

    // The feed API returns release (isRelease defaults to true) and listed versions only, unless told otherwise.
    private static readonly string[] PublicationFilters =
        ["", "&isListed=false", "&isRelease=false", "&isRelease=false&isListed=false"];
    private readonly AzureFeed _feed;
    private readonly HttpClient _http;
    private readonly HttpClient _anonymous;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private string? _packageBaseAddress;
    private Task<IReadOnlySet<string>>? _publicUpstreams;

    public string Origin => _feed.IndexUrl;

    public AzureArtifactsFeed(AzureFeed feed, string login, string pat, Action<RetryNotice>? onRetry = null)
        : this(feed, RepositoryHttpClient.Create(onRetry), RepositoryHttpClient.Create(onRetry))
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{pat}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    /// <summary>
    /// Uses <paramref name="http"/> as is, authentication included, and <paramref name="anonymous"/> for the
    /// redirects to a different host, such as the blob storage a package is downloaded from.
    /// </summary>
    public AzureArtifactsFeed(AzureFeed feed, HttpClient http, HttpClient anonymous)
    {
        _feed = feed;
        _http = http;
        _anonymous = anonymous;
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
        // packageNameQuery matches names containing the id: the package itself is the one named exactly so, and
        // may be on any page when many names contain the id.
        JsonElement? package = null;
        for (var skip = 0; package is null; skip += PageSize)
        {
            using var document = await ReadJsonAsync($"{_feed.PackagesApiUrl}?protocolType=NuGet" +
                $"&packageNameQuery={Uri.EscapeDataString(id)}&includeAllVersions=true" +
                $"&%24top={PageSize}&%24skip={skip}{filters}&api-version=7.1", ct);
            if (document is null) return [];
            var page = document.RootElement.GetProperty("value").EnumerateArray().ToArray();
            package = page.Where(p => p.TryGetProperty("name", out var name) &&
                    string.Equals(name.GetString(), id, StringComparison.OrdinalIgnoreCase))
                .Select(p => (JsonElement?)p.Clone()).FirstOrDefault();
            if (page.Length < PageSize) break;
        }
        if (package is not { ValueKind: JsonValueKind.Object } found ||
            !found.TryGetProperty("versions", out var versions))
            return [];

        // The feed definition is only read when a version comes from an upstream source.
        var upstreams = versions.EnumerateArray().Select(UpstreamSource).ToArray();
        var publicUpstreams = upstreams.Any(u => u is not null)
            ? await PublicUpstreamsAsync(ct)
            : new HashSet<string>();
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
                    : [],
                UpstreamSource(v) is { } upstream && publicUpstreams.Contains(upstream)))
            .Where(p => p.Version.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// The upstream source a version was saved from; null for a version published to the feed. Only public upstream
    /// sources (nuget.org...) make it a public package: another Azure Artifacts feed holds internal ones.
    /// </summary>
    private static string? UpstreamSource(JsonElement version) =>
        version.TryGetProperty("directUpstreamSourceId", out var upstream) &&
        upstream.ValueKind == JsonValueKind.String &&
        Guid.TryParse(upstream.GetString(), out var id) && id != Guid.Empty
            ? id.ToString("D") : null;

    /// <summary>The ids of the feed's public upstream sources, read once from the feed definition.</summary>
    private Task<IReadOnlySet<string>> PublicUpstreamsAsync(CancellationToken ct) =>
        _publicUpstreams ??= ReadPublicUpstreamsAsync(ct);

    private async Task<IReadOnlySet<string>> ReadPublicUpstreamsAsync(CancellationToken ct)
    {
        using var document = await ReadJsonAsync($"{_feed.FeedApiUrl}?api-version=7.1", ct);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document is null || !document.RootElement.TryGetProperty("upstreamSources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
            return ids;
        foreach (var source in sources.EnumerateArray())
            if (source.TryGetProperty("upstreamSourceType", out var type) &&
                string.Equals(type.GetString(), "public", StringComparison.OrdinalIgnoreCase) &&
                source.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var guid))
                ids.Add(guid.ToString("D"));
        return ids;
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
        return response is null ? null : await PackageDownload.ToTemporaryFileAsync(response.Content, ct);
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

    private static string Lower(string value) => PackageDownload.Lower(value);

    private static string Normalized(string version) => PackageDownload.Normalized(version);

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
        var response = await PackageDownload.GetAsync(_http, _anonymous, url, ct);
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
        if (!ReferenceEquals(_anonymous, _http)) _anonymous.Dispose();
        _indexLock.Dispose();
    }
}

/// <summary>nuget.org, read anonymously through its package content API (flat container). GET only.</summary>
public sealed class NuGetOrgFeed : IPackageFeed
{
    private const string PackageBase = "https://api.nuget.org/v3-flatcontainer/";
    private readonly HttpClient _http;

    public string Origin => "https://api.nuget.org/v3/index.json";

    public NuGetOrgFeed(Action<RetryNotice>? onRetry = null) : this(RepositoryHttpClient.Create(onRetry)) { }

    public NuGetOrgFeed(HttpClient http) => _http = http;

    /// <summary>nuget.org cannot list its packages: they are only read by id.</summary>
    public Task<IReadOnlyList<string>> GetPackageIdsAsync(CancellationToken ct) =>
        throw new NotSupportedException("nuget.org cannot list its packages.");

    public Task<IReadOnlyList<PackagePublication>> GetPublicationsAsync(string id, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PackagePublication>>([]);

    public async Task<IReadOnlyList<string>?> GetVersionsAsync(string id, CancellationToken ct)
    {
        using var response = await SendAsync($"{PackageBase}{PackageDownload.Lower(id)}/index.json", ct);
        if (response is null) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.GetProperty("versions").EnumerateArray()
            .Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToArray();
    }

    public async Task<string?> GetNuspecAsync(string id, string version, CancellationToken ct)
    {
        var (lower, normalized) = (PackageDownload.Lower(id), PackageDownload.Normalized(version));
        using var response = await SendAsync($"{PackageBase}{lower}/{normalized}/{lower}.nuspec", ct);
        return response is null ? null : await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<Stream?> GetPackageAsync(string id, string version, CancellationToken ct)
    {
        var (lower, normalized) = (PackageDownload.Lower(id), PackageDownload.Normalized(version));
        using var response = await SendAsync($"{PackageBase}{lower}/{normalized}/{lower}.{normalized}.nupkg", ct);
        return response is null ? null : await PackageDownload.ToTemporaryFileAsync(response.Content, ct);
    }

    /// <summary>The response, or null on HTTP 404.</summary>
    private async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken ct)
    {
        // nuget.org is read anonymously: its own client follows the redirects.
        var response = await PackageDownload.GetAsync(_http, _http, url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"nuget.org returned HTTP {status}.");
        }
        return response;
    }

    public void Dispose() => _http.Dispose();
}

internal static class PackageDownload
{
    private const int MaxRedirects = 5;

    /// <summary>
    /// A GET that follows redirects: Azure Artifacts responds to package download requests with an HTTP 303 redirect
    /// to blob storage. The clients are configured not to follow redirects, so that credentials are never forwarded to
    /// another host: a redirect to a different host is followed with <paramref name="anonymous"/>, and only over HTTPS.
    /// Returns the last response, a redirect when there are too many.
    /// </summary>
    public static async Task<HttpResponseMessage> GetAsync(HttpClient authenticated, HttpClient anonymous, string url,
        CancellationToken ct)
    {
        var origin = new Uri(url);
        var uri = origin;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // The credentials go to the feed's own authority only: same scheme, host and port.
            var sameAuthority = uri.Scheme == origin.Scheme && uri.Port == origin.Port &&
                string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase);
            var response = await (sameAuthority ? authenticated : anonymous)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) ||
                response.Headers.Location is not { } location || redirects == MaxRedirects)
                return response;
            response.Dispose();
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new HttpRequestException($"Redirected to an address that is not HTTPS: {uri.GetLeftPart(UriPartial.Path)}");
        }
    }

    // The NuGet package content API expects lowercase ids and versions.
    public static string Lower(string value) => Uri.EscapeDataString(value.ToLowerInvariant());

    // ...and normalized versions: 2.0 is 2.0.0, 1.0.0.0 is 1.0.0, build metadata is dropped.
    public static string Normalized(string version) =>
        Lower(NuGetVersion.TryParse(version, out var parsed) ? parsed.ToNormalizedString() : version);

    /// <summary>
    /// A package can be large and several are read at once: it goes to a temporary file, deleted when the
    /// stream is closed, rather than to memory. The ZIP reader needs a seekable stream.
    /// </summary>
    public static async Task<Stream> ToTemporaryFileAsync(HttpContent content, CancellationToken ct)
    {
        var file = new FileStream(Path.Combine(Path.GetTempPath(), $"depinv-{Guid.NewGuid():N}.nupkg"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await content.CopyToAsync(file, ct);
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }
}
