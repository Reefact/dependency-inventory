namespace DependencyScanner;

public static class BranchSelector
{
    private static readonly string[] Priorities = ["main", "release", "develop", "dev"];

    public static GitBranch? Select(IEnumerable<GitBranch> branches)
    {
        var available = branches
            .Where(b => !string.IsNullOrWhiteSpace(b.Name) &&
                !string.IsNullOrWhiteSpace(b.ObjectId) &&
                b.ObjectId.Any(c => c != '0'))
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var name in Priorities)
        {
            var exact = available.FirstOrDefault(b =>
                b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            // Les branches release/* sont considérées dans la famille release.
            if (name == "release")
            {
                var release = available.FirstOrDefault(b =>
                    b.Name.StartsWith("release/", StringComparison.OrdinalIgnoreCase));
                if (release is not null) return release;
            }
        }
        return available.FirstOrDefault();
    }
}
