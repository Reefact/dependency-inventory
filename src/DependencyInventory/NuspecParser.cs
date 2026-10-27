using System.IO.Compression;
using System.Xml.Linq;
using NuGet.Frameworks;

namespace DependencyInventory;

/// <summary>A dependency declared by a .nuspec; no framework when it applies to all of them.</summary>
public sealed record NuspecDependency(string? Framework, string Id, string? VersionRange, DependencyKind Kind);

/// <summary>What a .nuspec declares: its target frameworks and its dependencies.</summary>
public sealed record NuspecContent(IReadOnlyList<string> Frameworks, IReadOnlyList<NuspecDependency> Dependencies);

public static class NuspecParser
{
    /// <summary>
    /// Reads the dependency groups and the framework reference groups. Their target frameworks are the
    /// frameworks the package ships; a package without dependencies may declare none.
    /// </summary>
    public static NuspecContent Parse(string xml)
    {
        var metadata = XDocument.Parse(xml).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        var frameworks = new List<string>();
        var dependencies = new List<NuspecDependency>();
        foreach (var section in metadata?.Elements() ?? [])
        {
            var (child, kind, idAttribute) = section.Name.LocalName switch
            {
                "dependencies" => ("dependency", DependencyKind.Package, "id"),
                "frameworkReferences" => ("frameworkReference", DependencyKind.FrameworkReference, "name"),
                _ => (null, default, "")
            };
            if (child is null) continue;

            foreach (var element in section.Elements())
            {
                // An old .nuspec lists its dependencies without groups: they apply to every framework.
                var isGroup = element.Name.LocalName == "group";
                var framework = isGroup ? Normalize((string?)element.Attribute("targetFramework")) : null;
                if (framework is not null && !frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase))
                    frameworks.Add(framework);
                foreach (var dependency in isGroup ? element.Elements() : [element])
                {
                    if (dependency.Name.LocalName != child) continue;
                    var id = (string?)dependency.Attribute(idAttribute);
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    dependencies.Add(new NuspecDependency(framework, id.Trim(),
                        kind == DependencyKind.Package ? (string?)dependency.Attribute("version") : null, kind));
                }
            }
        }
        return new NuspecContent(frameworks, dependencies);
    }

    /// <summary>The frameworks of the lib/ and ref/ folders of a .nupkg, e.g. lib/net8.0/ gives net8.0.</summary>
    public static IReadOnlyList<string> FrameworksFromPackage(Stream nupkg)
    {
        using var archive = new ZipArchive(nupkg, ZipArchiveMode.Read);
        return archive.Entries
            .Select(e => e.FullName.Replace('\\', '/').Split('/'))
            .Where(parts => parts.Length >= 3 && parts[0] is "lib" or "ref" && parts[1].Length > 0)
            .Select(parts => Normalize(Uri.UnescapeDataString(parts[1]))!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The short moniker of a framework (.NETStandard2.0 gives netstandard2.0), or the value as is.</summary>
    internal static string? Normalize(string? framework)
    {
        if (string.IsNullOrWhiteSpace(framework)) return null;
        var parsed = NuGetFramework.Parse(framework.Trim());
        return parsed.IsUnsupported ? framework.Trim() : parsed.GetShortFolderName();
    }
}
