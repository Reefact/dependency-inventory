using System.Text.RegularExpressions;

namespace DependencyInventory;

/// <summary>
/// The branches to look for in a repository, in priority order: names, globs (<c>release/*</c>) and
/// <see cref="BranchPolicy.DefaultBranch"/>. A branch file may give some repositories their own list.
/// </summary>
public sealed class BranchPolicy
{
    /// <summary>Stands for the repository's default branch in a list.</summary>
    public const string DefaultBranch = "@default";

    private readonly IReadOnlyList<string>? _list;
    private readonly IReadOnlyList<(string Pattern, Regex Regex, IReadOnlyList<string> List)> _sections;

    private BranchPolicy(IReadOnlyList<string>? list,
        IReadOnlyList<(string Pattern, Regex Regex, IReadOnlyList<string> List)> sections, string? source)
    {
        _list = list;
        _sections = sections;
        Source = source;
    }

    /// <summary>No list: every repository is scanned on its default branch.</summary>
    public static BranchPolicy RepositoryDefault { get; } = new(null, [], null);

    /// <summary>Where the lists come from: the file read, or none for --branch and the default branch.</summary>
    public string? Source { get; }

    /// <summary>The same list for every repository, as given by --branch.</summary>
    public static BranchPolicy FromList(IEnumerable<string> branches)
    {
        var list = branches.Select(b => b.Trim()).ToArray();
        if (list.Length == 0 || list.Any(string.IsNullOrEmpty))
            throw new ArgumentException("A branch cannot be empty.");
        return new(list, [], null);
    }

    /// <summary>
    /// The policy given by the options: the --branch list, or the --branch-file, or the default branch of each
    /// repository without either.
    /// </summary>
    /// <exception cref="ArgumentException">Both options, or a missing file.</exception>
    /// <exception cref="FormatException">A malformed file.</exception>
    public static BranchPolicy FromOptions(IReadOnlyList<string>? branches, string? file)
    {
        var listed = branches is { Count: > 0 };
        if (listed && file is not null) throw new ArgumentException("Use --branch or --branch-file, not both.");
        if (listed) return FromList(branches!);
        if (file is null) return RepositoryDefault;
        if (!File.Exists(file)) throw new ArgumentException($"Branch file not found: {file}");
        return Parse(File.ReadAllText(file), file);
    }

    /// <summary>
    /// Reads a branch file: one name, glob or @default per line, in priority order; blank lines and lines starting
    /// with # are ignored. The lines before any section form the default list; a [pattern] line opens a section
    /// whose list applies to the repositories whose name matches the pattern.
    /// </summary>
    /// <exception cref="FormatException">A malformed or empty section, naming its line.</exception>
    public static BranchPolicy Parse(string text, string source)
    {
        List<string>? list = null;
        var sections = new List<(string Pattern, Regex Regex, IReadOnlyList<string> List)>();
        (string Pattern, int Line, List<string> List)? section = null;

        void CloseSection()
        {
            if (section is not { } open) return;
            if (open.List.Count == 0)
                throw new FormatException($"{source}, line {open.Line}: the section [{open.Pattern}] lists no branch.");
            sections.Add((open.Pattern, Glob(open.Pattern), open.List));
        }

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']') || line.Length < 3 || line[1..^1].Trim().Length == 0)
                    throw new FormatException(
                        $"{source}, line {index + 1}: a section is written [repository], a name or a glob.");
                CloseSection();
                section = (line[1..^1].Trim(), index + 1, []);
            }
            else if (section is { } open) open.List.Add(line);
            else (list ??= []).Add(line);
        }
        CloseSection();
        if (list is null && sections.Count == 0) throw new FormatException($"{source}: the file lists no branch.");
        return new(list, sections, source);
    }

    /// <summary>
    /// The list that applies to the repository and the section it comes from: the first matching section, in file
    /// order, else the default list. No list means the repository's default branch.
    /// </summary>
    public (IReadOnlyList<string>? List, string? Section) For(string repository)
    {
        foreach (var (pattern, regex, list) in _sections)
            if (regex.IsMatch(repository)) return (list, pattern);
        return (_list, null);
    }

    /// <summary>The branches looked for, as printed in the scan header.</summary>
    public string Describe()
    {
        var text = _list is null ? "the default branch of each repository"
            : string.Join(", ", _list) + " (the first that exists)";
        if (_sections.Count > 0)
            text += _sections.Count == 1 ? " (1 repository section)" : $" ({_sections.Count} repository sections)";
        return Source is null ? text : $"{text}, from {Source}";
    }

    // '*' = any sequence, '/' included; '?' = exactly one character; compared case-insensitively.
    internal static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
}

public static class BranchSelector
{
    /// <summary>
    /// The first branch of <paramref name="requested"/> that exists, in order: a name compared case-insensitively,
    /// the highest branch in natural order matching a glob (release/1.10 before release/1.9), or the repository's
    /// default branch for @default. No list selects the default branch. Branches without a commit are ignored.
    /// Null when none exists.
    /// </summary>
    public static GitBranch? Select(IEnumerable<GitBranch> branches, IReadOnlyList<string>? requested,
        string? defaultBranch)
    {
        var available = branches
            .Where(b => !string.IsNullOrWhiteSpace(b.Name) &&
                !string.IsNullOrWhiteSpace(b.ObjectId) &&
                b.ObjectId.Any(c => c != '0'))
            .ToArray();

        foreach (var entry in requested ?? [BranchPolicy.DefaultBranch])
        {
            GitBranch? found;
            if (entry.Equals(BranchPolicy.DefaultBranch, StringComparison.OrdinalIgnoreCase))
                found = defaultBranch is null ? null : Named(available, defaultBranch);
            else if (entry.IndexOfAny(['*', '?']) >= 0)
            {
                var glob = BranchPolicy.Glob(entry);
                found = available.Where(b => glob.IsMatch(b.Name))
                    .OrderByDescending(b => b.Name, NaturalComparer.Instance).FirstOrDefault();
            }
            else found = Named(available, entry);
            if (found is not null) return found;
        }
        return null;
    }

    private static GitBranch? Named(IEnumerable<GitBranch> branches, string name) =>
        branches.Where(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => b.Name, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>Compares digit runs as numbers and the rest case-insensitively: 1.9 before 1.10.</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null) return string.CompareOrdinal(x, y);
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var startX = i; var startY = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    var digitsX = x[startX..i].TrimStart('0'); var digitsY = y[startY..j].TrimStart('0');
                    var byNumber = digitsX.Length != digitsY.Length
                        ? digitsX.Length.CompareTo(digitsY.Length)
                        : string.CompareOrdinal(digitsX, digitsY);
                    if (byNumber != 0) return byNumber;
                }
                else
                {
                    var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (byChar != 0) return byChar;
                    i++; j++;
                }
            }
            var byLength = (x.Length - i).CompareTo(y.Length - j);
            return byLength != 0 ? byLength : string.CompareOrdinal(x, y);
        }
    }
}
