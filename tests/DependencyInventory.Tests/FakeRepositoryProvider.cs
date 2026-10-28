namespace DependencyInventory.Tests;

/// <summary>In-memory provider: branches and files per repository, plus paths whose read fails.</summary>
internal sealed class FakeRepositoryProvider : IRepositoryProvider
{
    private readonly Dictionary<string, IReadOnlyList<GitBranch>> _branches = new();
    private readonly Dictionary<string, Dictionary<string, string>> _files = new();
    private readonly Dictionary<string, Dictionary<string, string>> _commitFiles = new();
    private readonly Dictionary<(string Repository, string Path), Exception> _failingFiles = new();
    private readonly HashSet<string> _failingBranches = [];

    private int _inFlight;
    private int _maxInFlight;

    public List<Repository> Repositories { get; } = [];

    /// <summary>Paths of the files read, in any order.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> FilesRead { get; } = new();

    /// <summary>Delay of every call, so parallel calls overlap.</summary>
    public TimeSpan Latency { get; init; }

    /// <summary>Highest number of calls observed in progress at the same time.</summary>
    public int MaxConcurrentCalls => Volatile.Read(ref _maxInFlight);

    public string Origin => "https://dev.azure.com/acme/project";

    /// <summary>
    /// A repository whose default branch is <paramref name="branch"/>, unless <paramref name="noDefaultBranch"/>;
    /// <paramref name="otherBranches"/> exist too, with the same files.
    /// </summary>
    public FakeRepositoryProvider AddRepository(string name, string? branch = "main",
        IDictionary<string, string>? files = null, bool disabled = false, IEnumerable<string>? otherBranches = null,
        bool noDefaultBranch = false)
    {
        Repositories.Add(new Repository(name, name, "project", $"https://example/{name}", disabled,
            noDefaultBranch ? null : branch));
        _branches[name] = (branch is null ? [] : new[] { new GitBranch(branch, $"{name}-sha") })
            .Concat((otherBranches ?? []).Select(b => new GitBranch(b, $"{name}-{b}-sha"))).ToArray();
        _files[name] = new Dictionary<string, string>(files ?? new Dictionary<string, string>());
        return this;
    }

    /// <summary>
    /// Adds a branch on <paramref name="commit"/>; a new commit has its own <paramref name="files"/>, otherwise the
    /// commit's or the repository's files.
    /// </summary>
    public FakeRepositoryProvider AddBranch(string repository, string branch, string commit,
        IDictionary<string, string>? files = null)
    {
        _branches[repository] = [.. _branches[repository], new GitBranch(branch, commit)];
        if (files is not null) _commitFiles[commit] = new Dictionary<string, string>(files);
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

    // As in Git, the object id of a file is derived from its content.
    public Task<IReadOnlyList<GitFile>> GetTreeAsync(string repositoryId, string commitId, CancellationToken ct) =>
        CallAsync<IReadOnlyList<GitFile>>(() => Files(repositoryId, commitId)
            .Select(file => new GitFile(file.Key, false, Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(file.Value)))))
            .Append(new GitFile("/src", true))
            .ToArray(), ct);

    private Dictionary<string, string> Files(string repositoryId, string commitId) =>
        _commitFiles.TryGetValue(commitId, out var files) ? files : _files[repositoryId];

    public Task<string> GetFileAsync(string repositoryId, string commitId, string path, CancellationToken ct)
    {
        FilesRead.Enqueue(path);
        return CallAsync(() => _failingFiles.TryGetValue((repositoryId, path), out var error)
            ? throw error
            : Files(repositoryId, commitId)[path], ct);
    }

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
