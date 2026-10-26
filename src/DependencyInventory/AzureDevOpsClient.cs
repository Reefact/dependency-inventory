using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DependencyInventory;

public sealed class AzureDevOpsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly AzureOrigin _origin;

    public AzureDevOpsClient(AzureOrigin origin, string login, string pat)
    {
        _origin = origin;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{pat}"));
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", encoded);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DependencyInventory/1.0");
    }

    public async Task<IReadOnlyList<Repository>> GetRepositoriesAsync(CancellationToken ct)
    {
        using var document = await ReadJsonAsync(
            $"{_origin.RootUrl}/_apis/git/repositories?api-version=7.1", ct);
        return document.RootElement.GetProperty("value").EnumerateArray()
            .Select(r => new Repository(
                Get(r, "id"), Get(r, "name"),
                Get(r.GetProperty("project"), "name"),
                Get(r, "webUrl"),
                r.TryGetProperty("isDisabled", out var d) && d.ValueKind == JsonValueKind.True))
            .ToArray();
    }

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoId, CancellationToken ct)
    {
        var branches = new List<GitBranch>();
        var tokensSeen = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        do
        {
            var query = "filter=heads%2F&%24top=1000&api-version=7.1" +
                (continuation is null ? "" : "&continuationToken=" + Uri.EscapeDataString(continuation));
            using var response = await SendAsync($"{RepoApi(repoId)}/refs?{query}", ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            foreach (var value in document.RootElement.GetProperty("value").EnumerateArray())
            {
                var refName = Get(value, "name");
                if (refName.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
                    branches.Add(new GitBranch(refName[11..], Get(value, "objectId")));
            }
            continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var values)
                ? values.FirstOrDefault() : null;
            if (string.IsNullOrEmpty(continuation) &&
                document.RootElement.TryGetProperty("continuationToken", out var property) &&
                property.ValueKind == JsonValueKind.String)
                continuation = property.GetString();
            if (!string.IsNullOrEmpty(continuation) && !tokensSeen.Add(continuation))
                throw new InvalidOperationException("Pagination des branches en boucle.");
        } while (!string.IsNullOrEmpty(continuation));
        return branches;
    }

    public async Task<IReadOnlyList<GitFile>> GetTreeAsync(
        string repoId, string commitId, CancellationToken ct)
    {
        var query = "recursionLevel=Full&versionDescriptor.versionType=commit" +
            "&versionDescriptor.version=" + Uri.EscapeDataString(commitId) +
            "&api-version=7.1";
        using var document = await ReadJsonAsync($"{RepoApi(repoId)}/items?{query}", ct);
        return document.RootElement.GetProperty("value").EnumerateArray()
            .Select(f => new GitFile(
                Get(f, "path"),
                f.TryGetProperty("isFolder", out var folder) && folder.ValueKind == JsonValueKind.True))
            .ToArray();
    }

    public async Task<string> GetFileAsync(
        string repoId, string commitId, string path, CancellationToken ct)
    {
        var query = "path=" + Uri.EscapeDataString(path) +
            "&versionDescriptor.versionType=commit&versionDescriptor.version=" +
            Uri.EscapeDataString(commitId) +
            "&includeContent=true&%24format=json&api-version=7.1";
        using var document = await ReadJsonAsync($"{RepoApi(repoId)}/items?{query}", ct);
        var root = document.RootElement;
        var content = Get(root, "content");
        var encoding = Get(root, "contentType");
        return encoding.Equals("base64Encoded", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(content))
            : content;
    }

    public string FileUrl(Repository repo, string branch, string path) =>
        $"{_origin.RootUrl}/_git/{Uri.EscapeDataString(repo.Name)}?path={Uri.EscapeDataString(path)}" +
        $"&version=GB{Uri.EscapeDataString(branch)}";

    private string RepoApi(string repoId) =>
        $"{_origin.RootUrl}/_apis/git/repositories/{Uri.EscapeDataString(repoId)}";

    private async Task<JsonDocument> ReadJsonAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new HttpRequestException("Accès refusé : vérifier le PAT (Code: Read) et les droits sur le projet.");
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Azure DevOps a renvoyé HTTP {status}.");
        }
        return response;
    }

    private static string Get(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    public void Dispose() => _http.Dispose();
}
