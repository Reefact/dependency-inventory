using System.IO.Compression;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DependencyInventory.Tests;

/// <summary>Packages built in memory: real assemblies compiled from C#, in .nupkg files.</summary>
internal static class TestPackages
{
    private static readonly MetadataReference[] References =
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!,
            "System.Runtime.dll"))
    ];

    public static byte[] Assembly(string name, string source)
    {
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return buffer.ToArray();
    }

    public static string Nuspec(string id, string version, string dependencies = "", bool developmentDependency = false) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>{id}</id><version>{version}</version>
            {(developmentDependency ? "<developmentDependency>true</developmentDependency>" : "")}
            <dependencies>{dependencies}</dependencies>
          </metadata>
        </package>
        """;

    /// <summary>A .nupkg holding its .nuspec and the given files; an assembly source is compiled.</summary>
    public static byte[] Nupkg(string id, string version, string? nuspec = null,
        params (string Path, object Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, $"{id}.nuspec", Encoding.UTF8.GetBytes(nuspec ?? Nuspec(id, version)));
            foreach (var (path, content) in files)
                Write(archive, path, content switch
                {
                    byte[] bytes => bytes,
                    string source when path.EndsWith(".dll") => Assembly(Path.GetFileNameWithoutExtension(path), source),
                    string text => Encoding.UTF8.GetBytes(text),
                    _ => throw new ArgumentException(path)
                });
        }
        return buffer.ToArray();
    }

    private static void Write(ZipArchive archive, string path, byte[] content)
    {
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(content);
    }

    /// <summary>Adds a package version to a fake feed, published in it.</summary>
    public static FakePackageFeed AddPackage(this FakePackageFeed feed, string id, string version, byte[] nupkg) =>
        feed.Add(id, version, null, nupkg).Publish(id, version, DateTimeOffset.UnixEpoch, true);
}
