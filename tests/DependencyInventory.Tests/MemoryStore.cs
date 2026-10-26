namespace DependencyInventory.Tests;

/// <summary>In-memory vault that counts the changes made to it.</summary>
internal sealed class MemoryStore : ICredentialStore
{
    private readonly Dictionary<string, RepositoryCredentials> _entries = new();

    public int Writes { get; private set; }
    public int Deletes { get; private set; }
    public string Name => "memory";

    public RepositoryCredentials this[string key] { set => _entries[key] = value; }

    public RepositoryCredentials? Read(string key) => _entries.GetValueOrDefault(key);

    public void Write(string key, RepositoryCredentials credentials)
    {
        Writes++;
        _entries[key] = credentials;
    }

    public bool Delete(string key)
    {
        Deletes++;
        return _entries.Remove(key);
    }
}
