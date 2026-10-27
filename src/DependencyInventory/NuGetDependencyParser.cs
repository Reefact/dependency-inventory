using System.Xml.Linq;

namespace DependencyInventory;

public static class NuGetDependencyParser
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    /// <summary>True for .NET project files, the only files that can consume a central version.</summary>
    public static bool IsProjectFile(string path) =>
        ProjectExtensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    public static bool IsInterestingFile(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (new[] { "/bin/", "/obj/", "/node_modules/", "/.git/" }
            .Any(x => normalized.Contains(x, StringComparison.OrdinalIgnoreCase))) return false;

        return IsProjectFile(normalized) ||
            normalized.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/packages.config", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns all declared NuGet packages, including centrally declared versions.
    /// Does not evaluate MSBuild conditions or imports.
    /// </summary>
    public static IReadOnlyList<ParsedPackage> Parse(string path, string xml)
    {
        var document = XDocument.Parse(xml);
        var results = new List<ParsedPackage>();
        foreach (var element in document.Descendants())
        {
            var name = element.Name.LocalName;
            PackageKind? kind = name switch
            {
                "PackageReference" or "GlobalPackageReference" => PackageKind.Reference,
                "PackageVersion" => PackageKind.CentralVersion,
                "package" when path.EndsWith("packages.config", StringComparison.OrdinalIgnoreCase) => PackageKind.Reference,
                _ => null
            };
            if (kind is null) continue;

            var id = (string?)element.Attribute(name == "package" ? "id" : "Include") ??
                (string?)element.Attribute("Update");
            if (string.IsNullOrWhiteSpace(id)) continue;

            var version = (string?)element.Attribute("VersionOverride") ??
                (string?)element.Attribute("Version") ??
                (string?)element.Attribute("version") ??
                element.Elements().FirstOrDefault(x => x.Name.LocalName is "VersionOverride" or "Version")?.Value;
            results.Add(new ParsedPackage(id, version, kind.Value,
                version is null ? VersionSource.Unresolved : VersionSource.Explicit));
        }
        return results;
    }

    /// <summary>
    /// Returns the .NET target frameworks declared in a project or MSBuild file: the monikers of
    /// TargetFramework and TargetFrameworks (split on ';'), and TargetFrameworkVersion of legacy projects
    /// (v4.7.2 becomes net472). Values built from MSBuild properties are skipped; conditions are not evaluated.
    /// </summary>
    public static IReadOnlyList<string> ParseFrameworks(string path, string xml)
    {
        if (path.EndsWith("packages.config", StringComparison.OrdinalIgnoreCase)) return [];

        var frameworks = new List<string>();
        foreach (var element in XDocument.Parse(xml).Descendants())
        {
            var name = element.Name.LocalName;
            if (name is not ("TargetFramework" or "TargetFrameworks" or "TargetFrameworkVersion")) continue;

            var values = name == "TargetFrameworkVersion"
                ? new[] { LegacyMoniker(element.Value) }
                : element.Value.Split(';');
            foreach (var value in values.Select(x => x?.Trim()))
            {
                if (string.IsNullOrEmpty(value) || value.Contains("$(", StringComparison.Ordinal)) continue;
                if (!frameworks.Contains(value, StringComparer.OrdinalIgnoreCase)) frameworks.Add(value);
            }
        }
        return frameworks;
    }

    private static string? LegacyMoniker(string version)
    {
        var trimmed = version.Trim();
        if (trimmed.Length < 2 || !trimmed.StartsWith('v') || trimmed.Contains("$(", StringComparison.Ordinal))
            return null;
        return "net" + trimmed[1..].Replace(".", "");
    }

    public static string? ResolveCentralVersion(
        string projectFile,
        string package,
        IReadOnlyDictionary<string, IReadOnlyList<ParsedPackage>> files)
    {
        // Only the nearest Directory.Packages.props is automatically imported by NuGet.
        var projectDirectory = projectFile[..(projectFile.LastIndexOf('/') + 1)];
        var centralFiles = files
            .Where(f => f.Key.EndsWith("/Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
            .Where(f => projectDirectory.StartsWith(
                f.Key[..(f.Key.LastIndexOf('/') + 1)], StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Key.Length);

        var closest = centralFiles.FirstOrDefault();
        return closest.Value?
            .FirstOrDefault(p => p.Kind == PackageKind.CentralVersion &&
                p.Id.Equals(package, StringComparison.OrdinalIgnoreCase))?.Version;
    }
}
