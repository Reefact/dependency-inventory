using System.Text;

namespace DependencyInventory;

/// <summary>
/// Login is used for Azure DevOps Basic/PAT authentication.
/// GitLab only needs the PAT and leaves Login empty.
/// </summary>
public sealed record RepositoryCredentials(string Login, string Pat);

public static class CredentialsFile
{
    public static string DefaultFileName(SourceProvider provider) => provider switch
    {
        SourceProvider.Azure => ".azure-credentials",
        SourceProvider.GitLab => ".gitlab-credentials",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    public static string ResolvePath(SourceProvider provider, string? explicitPath) =>
        string.IsNullOrWhiteSpace(explicitPath) ? DefaultFileName(provider) : explicitPath;

    public static async Task<RepositoryCredentials> ReadAsync(
        string path, SourceProvider provider, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The credentials file path is empty.");

        var contents = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        return Parse(contents, provider);
    }

    public static RepositoryCredentials Parse(string contents, SourceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var lines = contents.TrimStart('\uFEFF')
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(x => x.Trim()).ToList();

        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        switch (provider)
        {
            case SourceProvider.Azure:
                if (lines.Count != 2 || lines[0].Length == 0 || lines[1].Length == 0)
                    throw new FormatException(
                        "Azure: expected a file of two non-empty lines: the login, then the PAT.");
                return new RepositoryCredentials(lines[0], lines[1]);

            case SourceProvider.GitLab:
                if (lines.Count != 1 || lines[0].Length == 0)
                    throw new FormatException(
                        "GitLab: expected a file of one non-empty line: the PAT.");
                return new RepositoryCredentials("", lines[0]);

            default:
                throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }
}
