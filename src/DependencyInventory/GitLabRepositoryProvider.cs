using System.Net;
using System.Text.Json;

namespace DependencyInventory;

/// <summary>
/// GitLab group URL (for example https://gitlab.example.com/group/subgroup).
/// This is a group, not an individual project/repository.
/// </summary>
public sealed record GitLabOrigin(string BaseUrl, string GroupPath)
{
    public string GroupUrl => $"{BaseUrl}/{GroupPath}";

    public static GitLabOrigin Parse(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) ||
            uri.UserInfo.Length != 0 ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("URL GitLab attendue : https://gitlab.example.com/groupe/sous-groupe");

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();

        if (parts.Length == 0 || parts.Any(p => string.IsNullOrWhiteSpace(p) ||
            p is "." or ".." || p.Contains('/') || p.Contains('\\')) ||
            (parts.Length >= 2 && parts[0] == "api" && parts[1] == "v4"))
            throw new ArgumentException("L'URL GitLab doit désigner un groupe (ou sous-groupe).");

        return new GitLabOrigin(uri.GetLeftPart(UriPartial.Authority), string.Join("/", parts));
    }
}

/// <summary>Read-only GitLab REST API v4 repository provider.</summary>
public sealed class GitLabRepositoryProvider : IRepositoryProvider
{
    private readonly HttpClient _http;
    private readonly GitLabOrigin _origin;
    private readonly string _apiBase;

    public string Origin => _origin.GroupUrl;

    public GitLabRepositoryProvider(GitLabOrigin origin, string pat)
    {
        if (string.IsNullOrWhiteSpace(pat))
            throw new ArgumentException("Un jeton d'accès GitLab est nécessaire.");

        _origin = origin;
        _apiBase = origin.BaseUrl + "/api/v4";
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        // PRIVATE-TOKEN is intentionally never logged or serialized.
        _http.DefaultRequestHeaders.TryAddWithoutValidation("PRIVATE-TOKEN", pat);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DependencyInventory/1.0");
    }

    public async Task<IReadOnlyList<Repository>> GetRepositoriesAsync(CancellationToken ct)
    {
        var path = Uri.EscapeDataString(_origin.GroupPath);
        var projects = await GetPaginatedAsync(
            $"{_apiBase}/groups/{path}/projects?include_subgroups=true&with_shared=false", ct);
        return projects.Select(p =>
        {
            var group = p.TryGetProperty("namespace", out var ns) ? GetString(ns, "full_path") : "";
            var archived = p.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True;
            return new Repository(
                p.GetProperty("id").ToString(),
                GetString(p, "name"),
                group,
                GetString(p, "web_url"),
                archived);
        }).ToArray();
    }

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryId, CancellationToken ct)
    {
        var branches = await GetPaginatedAsync(
            $"{RepoApi(repositoryId)}/repository/branches", ct);
        return branches.Select(b => new GitBranch(
            GetString(b, "name"),
            b.TryGetProperty("commit", out var commit) ? GetString(commit, "id") : ""
        )).ToArray();
    }

    public async Task<IReadOnlyList<GitFile>> GetTreeAsync(
        string repositoryId, string commitId, CancellationToken ct)
    {
        var tree = await GetPaginatedAsync(
            $"{RepoApi(repositoryId)}/repository/tree?recursive=true&ref={Uri.EscapeDataString(commitId)}", ct);
        return tree.Select(e => new GitFile(
            "/" + GetString(e, "path").TrimStart('/'),
            GetString(e, "type") == "tree")).ToArray();
    }

    public async Task<string> GetFileAsync(
        string repositoryId, string commitId, string path, CancellationToken ct)
    {
        var filePath = Uri.EscapeDataString(path.TrimStart('/'));
        using var response = await SendAsync(
            $"{RepoApi(repositoryId)}/repository/files/{filePath}/raw?ref={Uri.EscapeDataString(commitId)}",
            ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public string FileUrl(Repository repository, string commitId, string path)
    {
        var encodedPath = string.Join("/", path.Trim('/').Split('/').Select(Uri.EscapeDataString));
        return $"{repository.WebUrl}/-/blob/{Uri.EscapeDataString(commitId)}/{encodedPath}";
    }

    private string RepoApi(string id) => $"{_apiBase}/projects/{Uri.EscapeDataString(id)}";

    // Offset pagination supported by the GitLab groups, branches and tree endpoints.
    private async Task<List<JsonElement>> GetPaginatedAsync(string endpoint, CancellationToken ct)
    {
        var results = new List<JsonElement>();
        for (var page = 1; page <= 10000; page++)
        {
            var joiner = endpoint.Contains('?') ? "&" : "?";
            using var response = await SendAsync($"{endpoint}{joiner}per_page=100&page={page}", ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Réponse GitLab inattendue : tableau JSON attendu.");

            var values = doc.RootElement.EnumerateArray().ToArray();
            results.AddRange(values.Select(v => v.Clone()));
            if (values.Length < 100) return results;
        }
        throw new InvalidOperationException("Limite de pagination GitLab atteinte.");
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new HttpRequestException("GitLab : accès refusé. Vérifier le PAT (read_api) et les droits sur le groupe.");
        }
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"GitLab : HTTP {code} (groupe, dépôt ou fichier inaccessible).");
        }
        return response;
    }

    private static string GetString(JsonElement e, string key) =>
        e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    public void Dispose() => _http.Dispose();
}
