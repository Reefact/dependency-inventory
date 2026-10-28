namespace DependencyInventory;

public enum SourceProvider
{
    Azure,
    GitLab
}

/// <summary>Common read-only Git repository discovery abstraction.</summary>
public interface IRepositoryProvider : IDisposable
{
    string Origin { get; }

    Task<IReadOnlyList<Repository>> GetRepositoriesAsync(CancellationToken ct);
    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryId, CancellationToken ct);
    Task<IReadOnlyList<GitFile>> GetTreeAsync(string repositoryId, string commitId, CancellationToken ct);
    Task<string> GetFileAsync(string repositoryId, string commitId, string path, CancellationToken ct);
    string FileUrl(Repository repository, string commitId, string path);
}

public static class RepositoryProviderFactory
{
    public static IRepositoryProvider Create(SourceProvider provider, string url,
        RepositoryCredentials credentials, Action<RetryNotice>? onRetry = null) =>
        provider switch
        {
            SourceProvider.Azure => new AzureRepositoryProvider(
                AzureOrigin.Parse(url), credentials.Login, credentials.Pat, onRetry),
            SourceProvider.GitLab => new GitLabRepositoryProvider(
                GitLabOrigin.Parse(url), credentials.Pat, onRetry),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

    public static void ValidateOrigin(SourceProvider provider, string url)
    {
        switch (provider)
        {
            case SourceProvider.Azure: _ = AzureOrigin.Parse(url); break;
            case SourceProvider.GitLab: _ = GitLabOrigin.Parse(url); break;
            default: throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }
}

public sealed class AzureRepositoryProvider : IRepositoryProvider
{
    private readonly AzureDevOpsClient _api;
    private readonly AzureOrigin _origin;

    public string Origin => _origin.RootUrl;

    public AzureRepositoryProvider(AzureOrigin origin, string login, string pat, Action<RetryNotice>? onRetry = null)
    {
        _origin = origin;
        _api = new AzureDevOpsClient(origin, login, pat, onRetry);
    }

    public Task<IReadOnlyList<Repository>> GetRepositoriesAsync(CancellationToken ct) =>
        _api.GetRepositoriesAsync(ct);

    public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryId, CancellationToken ct) =>
        _api.GetBranchesAsync(repositoryId, ct);

    public Task<IReadOnlyList<GitFile>> GetTreeAsync(string repositoryId, string commitId, CancellationToken ct) =>
        _api.GetTreeAsync(repositoryId, commitId, ct);

    public Task<string> GetFileAsync(string repositoryId, string commitId, string path, CancellationToken ct) =>
        _api.GetFileAsync(repositoryId, commitId, path, ct);

    public string FileUrl(Repository repository, string commitId, string path) =>
        $"{_origin.RootUrl}/_git/{Uri.EscapeDataString(repository.Name)}" +
        $"?path={Uri.EscapeDataString(path)}&version=GC{Uri.EscapeDataString(commitId)}";

    public void Dispose() => _api.Dispose();
}

/// <summary>
/// Reads each file content once across the commits of one repository: a file whose content id, given by the tree,
/// was already read is not downloaded again. Meant for the branches of one repository, then discarded; it does not
/// own the provider it wraps.
/// </summary>
public sealed class ContentReuseProvider(IRepositoryProvider inner) : IRepositoryProvider
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Commit, string Path), string> _ids =
        new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<string>>> _contents =
        new(StringComparer.Ordinal);

    public string Origin => inner.Origin;

    public Task<IReadOnlyList<Repository>> GetRepositoriesAsync(CancellationToken ct) =>
        inner.GetRepositoriesAsync(ct);

    public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryId, CancellationToken ct) =>
        inner.GetBranchesAsync(repositoryId, ct);

    public async Task<IReadOnlyList<GitFile>> GetTreeAsync(string repositoryId, string commitId,
        CancellationToken ct)
    {
        var tree = await inner.GetTreeAsync(repositoryId, commitId, ct);
        foreach (var file in tree)
            if (!file.IsFolder && file.ObjectId is { Length: > 0 } id) _ids[(commitId, file.Path)] = id;
        return tree;
    }

    public async Task<string> GetFileAsync(string repositoryId, string commitId, string path, CancellationToken ct)
    {
        if (!_ids.TryGetValue((commitId, path), out var id))
            return await inner.GetFileAsync(repositoryId, commitId, path, ct);
        var read = _contents.GetOrAdd(id, _ => new Lazy<Task<string>>(
            () => inner.GetFileAsync(repositoryId, commitId, path, ct)));
        try { return await read.Value; }
        catch
        {
            // A failed or cancelled read is not kept: another commit may read the same content again.
            _contents.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(id, read));
            throw;
        }
    }

    public string FileUrl(Repository repository, string commitId, string path) =>
        inner.FileUrl(repository, commitId, path);

    public void Dispose() { }
}
