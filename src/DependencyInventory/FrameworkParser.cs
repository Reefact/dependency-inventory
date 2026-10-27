using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DependencyInventory;

/// <summary>
/// Target frameworks declared by one file. <see cref="Unresolved"/> is true when a declaration could not be read
/// literally, e.g. a value built from an MSBuild property.
/// </summary>
public sealed record DeclaredFrameworks(IReadOnlyList<string> Frameworks, bool Unresolved)
{
    public static readonly DeclaredFrameworks None = new([], false);

    public bool DeclaresNothing => Frameworks.Count == 0 && !Unresolved;
}

/// <summary>One target framework of a project; no framework when it could not be resolved.</summary>
public sealed record ResolvedFramework(string? Framework, FrameworkSource Source);

public static class FrameworkParser
{
    private const string BuildProps = "/Directory.Build.props";

    /// <summary>Project files and Directory.Build.props, the file a project inherits its framework from.</summary>
    public static bool IsInterestingFile(string path)
    {
        var normalized = path.Replace('\\', '/');
        return !NuGetDependencyParser.IsInExcludedFolder(normalized) &&
            (NuGetDependencyParser.IsProjectFile(normalized) ||
                normalized.EndsWith(BuildProps, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads TargetFramework, TargetFrameworks (split on ';') and the legacy TargetFrameworkVersion, whose
    /// v4.7.2 becomes net472. Conditions are not evaluated: every declared value is returned.
    /// </summary>
    public static DeclaredFrameworks Parse(string path, string xml)
    {
        var frameworks = new List<string>();
        var unresolved = false;
        foreach (var element in XDocument.Parse(xml).Descendants())
        {
            IEnumerable<string?> values = element.Name.LocalName switch
            {
                "TargetFramework" or "TargetFrameworks" => element.Value.Split(';'),
                "TargetFrameworkVersion" => new[] { LegacyMoniker(element.Value) },
                _ => []
            };
            foreach (var value in values)
            {
                var framework = value?.Trim();
                if (framework is null || framework.Contains('$')) unresolved = true;
                else if (framework.Length > 0 && !frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase))
                    frameworks.Add(framework);
            }
        }
        return new DeclaredFrameworks(frameworks, unresolved);
    }

    /// <summary>
    /// The frameworks of a project: its own declarations, otherwise those of the nearest Directory.Build.props
    /// (the only one MSBuild imports automatically). <paramref name="files"/> holds every file found, null for
    /// a file that could not be read; an unreadable project is unresolved. Always returns at least one entry.
    /// </summary>
    public static IReadOnlyList<ResolvedFramework> Resolve(
        string projectFile, IReadOnlyDictionary<string, DeclaredFrameworks?> files)
    {
        if (files.TryGetValue(projectFile, out var own) && own is null)
            return [new ResolvedFramework(null, FrameworkSource.Unresolved)];
        if (own is { DeclaresNothing: false }) return Entries(own, FrameworkSource.Project);

        // Git folders are case-sensitive: /src/App/ is not a parent of /src/app/Api.csproj.
        var projectDirectory = projectFile[..(projectFile.LastIndexOf('/') + 1)];
        var nearest = files
            .Where(f => f.Key.EndsWith(BuildProps, StringComparison.OrdinalIgnoreCase))
            .Where(f => projectDirectory.StartsWith(
                f.Key[..(f.Key.LastIndexOf('/') + 1)], StringComparison.Ordinal))
            .OrderByDescending(f => f.Key.Length)
            .Select(f => f.Value)
            .FirstOrDefault();
        return nearest is { DeclaresNothing: false }
            ? Entries(nearest, FrameworkSource.DirectoryBuildProps)
            : [new ResolvedFramework(null, FrameworkSource.Unresolved)];
    }

    private static IReadOnlyList<ResolvedFramework> Entries(DeclaredFrameworks declared, FrameworkSource source) =>
        declared.Frameworks.Select(f => new ResolvedFramework(f, source))
            .Concat(declared.Unresolved ? [new ResolvedFramework(null, FrameworkSource.Unresolved)] : [])
            .ToArray();

    private static string? LegacyMoniker(string version)
    {
        var match = Regex.Match(version.Trim(), @"^v(\d+(?:\.\d+)*)$");
        return match.Success ? "net" + match.Groups[1].Value.Replace(".", "") : null;
    }
}
