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
    public static IRepositoryProvider Create(SourceProvider provider, string url, RepositoryCredentials credentials) =>
        provider switch
        {
            SourceProvider.Azure => new AzureRepositoryProvider(
                AzureOrigin.Parse(url), credentials.Login, credentials.Pat),
            SourceProvider.GitLab => new GitLabRepositoryProvider(
                GitLabOrigin.Parse(url), credentials.Pat),
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

    public AzureRepositoryProvider(AzureOrigin origin, string login, string pat)
    {
        _origin = origin;
        _api = new AzureDevOpsClient(origin, login, pat);
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
