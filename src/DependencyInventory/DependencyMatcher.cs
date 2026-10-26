using System.Text.RegularExpressions;

namespace DependencyInventory;

public sealed record DependencyFilter(string Type, string Expression)
{
    public override string ToString() => $"{Type}:{Expression}";
}

/// <summary>
/// Matches NuGet package IDs against any of the configured glob and/or regex filters.
/// Globs match the whole ID, regular expressions use normal .NET search semantics.
/// </summary>
public sealed class DependencyMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);
    private readonly List<(DependencyFilter Filter, Regex Regex)> _rules;

    private DependencyMatcher(List<(DependencyFilter Filter, Regex Regex)> rules) =>
        _rules = rules;

    public IReadOnlyList<DependencyFilter> Filters =>
        _rules.Select(r => r.Filter).ToArray();

    public static DependencyMatcher Create(
        IEnumerable<string>? globs,
        IEnumerable<string>? expressions,
        bool caseSensitive = false)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;

        var rules = new List<(DependencyFilter, Regex)>();

        foreach (var pattern in globs ?? [])
        {
            if (string.IsNullOrWhiteSpace(pattern))
                throw new ArgumentException("Un glob ne peut pas être vide.");

            // '*' = any sequence, '?' = exactly one character, other characters literal.
            var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            rules.Add((new DependencyFilter("Glob", pattern),
                new Regex(regex, options, RegexTimeout)));
        }

        foreach (var expression in expressions ?? [])
        {
            if (string.IsNullOrWhiteSpace(expression))
                throw new ArgumentException("Une expression régulière ne peut pas être vide.");

            try
            {
                rules.Add((new DependencyFilter("Regex", expression),
                    new Regex(expression, options, RegexTimeout)));
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    $"Expression régulière invalide '{expression}' : {exception.Message}",
                    nameof(expressions), exception);
            }
        }

        if (rules.Count == 0)
            throw new ArgumentException("Indiquer au moins un --pattern ou un --regex.");

        return new DependencyMatcher(rules);
    }

    /// <summary>Returns all matching filter names; no match means an empty collection.</summary>
    public IReadOnlyList<string> Match(string packageId) =>
        _rules.Where(rule => rule.Regex.IsMatch(packageId))
            .Select(rule => rule.Filter.ToString())
            .ToArray();
}
