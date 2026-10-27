using System.Xml.Linq;

namespace DependencyInventory;

public static class NuGetDependencyParser
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    /// <summary>True for .NET project files, the only files that can consume a central version.</summary>
    public static bool IsProjectFile(string path) =>
        ProjectExtensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>Build output and tooling folders, never scanned. Expects '/' separators.</summary>
    internal static bool IsInExcludedFolder(string path) =>
        new[] { "/bin/", "/obj/", "/node_modules/", "/.git/" }
            .Any(x => path.Contains(x, StringComparison.OrdinalIgnoreCase));

    public static bool IsInterestingFile(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (IsInExcludedFolder(normalized)) return false;

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

    public static string? ResolveCentralVersion(
        string projectFile,
        string package,
        IReadOnlyDictionary<string, IReadOnlyList<ParsedPackage>> files)
    {
        // Only the nearest Directory.Packages.props is automatically imported by NuGet.
        // Git folders are case-sensitive: /src/App/ is not a parent of /src/app/Api.csproj.
        var projectDirectory = projectFile[..(projectFile.LastIndexOf('/') + 1)];
        var centralFiles = files
            .Where(f => f.Key.EndsWith("/Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
            .Where(f => projectDirectory.StartsWith(
                f.Key[..(f.Key.LastIndexOf('/') + 1)], StringComparison.Ordinal))
            .OrderByDescending(f => f.Key.Length);

        var closest = centralFiles.FirstOrDefault();
        return closest.Value?
            .FirstOrDefault(p => p.Kind == PackageKind.CentralVersion &&
                p.Id.Equals(package, StringComparison.OrdinalIgnoreCase))?.Version;
    }
}
