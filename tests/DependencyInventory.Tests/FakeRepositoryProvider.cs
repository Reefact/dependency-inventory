namespace DependencyInventory.Tests;

/// <summary>In-memory provider: branches and files per repository, plus paths whose read fails.</summary>
internal sealed class FakeRepositoryProvider : IRepositoryProvider
{
    private readonly Dictionary<string, IReadOnlyList<GitBranch>> _branches = new();
    private readonly Dictionary<string, Dictionary<string, string>> _files = new();
    private readonly Dictionary<(string Repository, string Path), Exception> _failingFiles = new();
    private readonly HashSet<string> _failingBranches = [];

    private int _inFlight;
    private int _maxInFlight;

    public List<Repository> Repositories { get; } = [];

    /// <summary>Delay of every call, so parallel calls overlap.</summary>
    public TimeSpan Latency { get; init; }

    /// <summary>Highest number of calls observed in progress at the same time.</summary>
    public int MaxConcurrentCalls => Volatile.Read(ref _maxInFlight);

    public string Origin => "https://dev.azure.com/acme/project";

    public FakeRepositoryProvider AddRepository(string name, string? branch = "main",
        IDictionary<string, string>? files = null, bool disabled = false)
    {
        Repositories.Add(new Repository(name, name, "project", $"https://example/{name}", disabled));
        _branches[name] = branch is null ? [] : [new GitBranch(branch, $"{name}-sha")];
        _files[name] = new Dictionary<string, string>(files ?? new Dictionary<string, string>());
        return this;
    }

    public FakeRepositoryProvider FailFile(string repository, string path, Exception? error = null)
    {
        _failingFiles[(repository, path)] = error ?? new HttpRequestException($"cannot read {path}");
        return this;
    }

    public FakeRepositoryProvider FailBranches(string repository)
    {
        _failingBranches.Add(repository);
        return this;
    }

    public Task<IReadOnlyList<Repository>> GetRepositoriesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Repository>>(Repositories);

    public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryId, CancellationToken ct) =>
        CallAsync(() => _failingBranches.Contains(repositoryId)
            ? throw new HttpRequestException("branches unavailable")
            : _branches[repositoryId], ct);

    public Task<IReadOnlyList<GitFile>> GetTreeAsync(string repositoryId, string commitId, CancellationToken ct) =>
        CallAsync<IReadOnlyList<GitFile>>(() => _files[repositoryId].Keys
            .Select(path => new GitFile(path, false))
            .Append(new GitFile("/src", true))
            .ToArray(), ct);

    public Task<string> GetFileAsync(string repositoryId, string commitId, string path, CancellationToken ct) =>
        CallAsync(() => _failingFiles.TryGetValue((repositoryId, path), out var error)
            ? throw error
            : _files[repositoryId][path], ct);

    private async Task<T> CallAsync<T>(Func<T> call, CancellationToken ct)
    {
        var inFlight = Interlocked.Increment(ref _inFlight);
        try
        {
            int max;
            while (inFlight > (max = Volatile.Read(ref _maxInFlight)) &&
                Interlocked.CompareExchange(ref _maxInFlight, inFlight, max) != max) { }
            if (Latency > TimeSpan.Zero) await Task.Delay(Latency, ct);
            return call();
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public string FileUrl(Repository repository, string commitId, string path) =>
        $"{repository.WebUrl}/{commitId}{path}";

    public void Dispose() { }
}
