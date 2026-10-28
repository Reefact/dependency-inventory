using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DependencyInventory;

/// <summary>A public type of a package; its name has no generic arity (List`1 is List).</summary>
public sealed record PackageType(string Namespace, string Name)
{
    public string FullName => Namespace.Length == 0 ? Name : $"{Namespace}.{Name}";
}

/// <summary>An extension method of a package, the namespace that brings it in scope and its declaring class.</summary>
public sealed record ExtensionMethod(string Name, string Namespace, string DeclaringType);

/// <summary>
/// What a package version exposes to the code that references it: its public types and extension methods, read
/// from its assemblies, and what the usage rules need besides: build assets, development dependency, dependencies.
/// Kept in the local cache as JSON: property names are part of its format.
/// </summary>
public sealed record PackageContent(
    IReadOnlyList<PackageType> Types,
    IReadOnlyList<ExtensionMethod> ExtensionMethods,
    bool HasBuildAssets,
    bool DevelopmentDependency,
    IReadOnlyList<NuspecDependency> Dependencies)
{
    public bool HasTypes => Types.Count > 0 || ExtensionMethods.Count > 0;
}

public static class PackageContentReader
{
    // Folders whose content acts on the build without being referenced by the code.
    private static readonly string[] BuildFolders =
        ["analyzers", "build", "buildTransitive", "buildMultiTargeting", "tools", "content", "contentFiles"];

    /// <summary>Reads a .nupkg.</summary>
    public static PackageContent ReadPackage(Stream nupkg)
    {
        using var archive = new ZipArchive(nupkg, ZipArchiveMode.Read);
        return Read(archive.Entries.Select(e => (e.FullName, (Func<Stream>)e.Open)));
    }

    /// <summary>Reads a package extracted by NuGet, e.g. in the global packages folder.</summary>
    public static PackageContent ReadFolder(string folder) =>
        Read(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(folder, f), (Func<Stream>)(() => File.OpenRead(f)))));

    /// <summary>
    /// Reads the .nuspec at the root and the assemblies of every target framework: those of ref/{framework}/
    /// when that folder has any, otherwise those of lib/{framework}/.
    /// </summary>
    private static PackageContent Read(IEnumerable<(string Path, Func<Stream> Open)> files)
    {
        var entries = files.Select(f => (Parts: Uri.UnescapeDataString(f.Path.Replace('\\', '/')).Split('/'), f.Open))
            .ToArray();
        NuspecContent? nuspec = null;
        if (entries.FirstOrDefault(e => e.Parts.Length == 1 &&
                e.Parts[0].EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)) is { Open: { } openNuspec })
        {
            using var reader = new StreamReader(openNuspec());
            nuspec = NuspecParser.Parse(reader.ReadToEnd());
        }

        // Satellite assemblies (lib/net8.0/fr/A.resources.dll) are one level deeper and left out.
        var assemblies = entries
            .Where(e => e.Parts.Length == 3 && e.Parts[0] is "lib" or "ref" &&
                e.Parts[2].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.Parts[1], StringComparer.OrdinalIgnoreCase)
            .SelectMany(framework => framework.Any(e => e.Parts[0] == "ref")
                ? framework.Where(e => e.Parts[0] == "ref") : framework);

        var types = new HashSet<PackageType>();
        var extensions = new HashSet<ExtensionMethod>();
        foreach (var assembly in assemblies)
        {
            using var stream = new MemoryStream();
            using (var source = assembly.Open()) source.CopyTo(stream);
            stream.Position = 0;
            ReadAssembly(stream, types, extensions);
        }

        return new PackageContent(
            types.OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray(),
            extensions.OrderBy(e => e.DeclaringType, StringComparer.Ordinal)
                .ThenBy(e => e.Name, StringComparer.Ordinal).ToArray(),
            entries.Any(e => e.Parts.Length > 1 && BuildFolders.Contains(e.Parts[0], StringComparer.OrdinalIgnoreCase)),
            string.Equals(nuspec?.Metadata.DevelopmentDependency?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            nuspec?.Dependencies.Where(d => d.Kind == DependencyKind.Package).ToArray() ?? []);
    }

    /// <summary>The public top-level types and the extension methods of an assembly; nothing when it is not one.</summary>
    private static void ReadAssembly(Stream stream, HashSet<PackageType> types, HashSet<ExtensionMethod> extensions)
    {
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return;
        MetadataReader metadata;
        try { metadata = pe.GetMetadataReader(); }
        catch (BadImageFormatException) { return; }

        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            // Nested types are reached through their public parent, which is listed.
            if ((type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public) continue;
            var name = metadata.GetString(type.Name);
            if (name.StartsWith('<')) continue;
            var arity = name.IndexOf('`');
            var packageType = new PackageType(metadata.GetString(type.Namespace), arity < 0 ? name : name[..arity]);
            types.Add(packageType);

            // Extension methods are declared by static classes, which are abstract and sealed in metadata.
            const TypeAttributes staticClass = TypeAttributes.Abstract | TypeAttributes.Sealed;
            if ((type.Attributes & staticClass) != staticClass || !IsExtension(metadata, type.GetCustomAttributes()))
                continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if ((method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public &&
                    IsExtension(metadata, method.GetCustomAttributes()))
                    extensions.Add(new ExtensionMethod(metadata.GetString(method.Name), packageType.Namespace,
                        packageType.FullName));
            }
        }
    }

    private static bool IsExtension(MetadataReader metadata, CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var constructor = metadata.GetCustomAttribute(handle).Constructor;
            var (ns, name) = constructor.Kind switch
            {
                HandleKind.MemberReference when metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent is
                    { Kind: HandleKind.TypeReference } parent =>
                    metadata.GetTypeReference((TypeReferenceHandle)parent) is var reference
                        ? (metadata.GetString(reference.Namespace), metadata.GetString(reference.Name)) : default,
                HandleKind.MethodDefinition =>
                    metadata.GetTypeDefinition(metadata.GetMethodDefinition((MethodDefinitionHandle)constructor)
                        .GetDeclaringType()) is var definition
                        ? (metadata.GetString(definition.Namespace), metadata.GetString(definition.Name)) : default,
                _ => default((string?, string?))
            };
            if (ns == "System.Runtime.CompilerServices" && name == "ExtensionAttribute") return true;
        }
        return false;
    }
}
