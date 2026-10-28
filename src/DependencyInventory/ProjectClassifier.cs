using System.Xml.Linq;

namespace DependencyInventory;

/// <summary>
/// What a project file or a Directory.Build.props says about the kind of project: its SDK, OutputType,
/// IsTestProject, AzureFunctionsVersion, the legacy ProjectTypeGuids and its test framework references. Conditions
/// are not evaluated: the last value declared wins. A property is null when the file does not declare it, and empty
/// when the file declares it empty, which overrides an inherited value as in MSBuild.
/// </summary>
public sealed record ProjectTraits(IReadOnlyList<string> Sdks, string? OutputType, string? IsTestProject,
    string? AzureFunctionsVersion, IReadOnlyList<string> ProjectTypeGuids, bool ReferencesTestFramework)
{
    public static readonly ProjectTraits None = new([], null, null, null, [], false);

    private static readonly string[] TestFrameworks =
        ["Microsoft.NET.Test.Sdk", "xunit", "xunit.core", "NUnit", "nunit.framework", "MSTest.TestFramework",
            "Microsoft.VisualStudio.QualityTools.UnitTestFramework"];

    public static ProjectTraits Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root;
        string? Last(string name) =>
            document.Descendants().LastOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

        // Sdk="A;B/1.0", <Sdk Name="A" /> and <Import Sdk="A" />: every SDK, without its version.
        var sdks = new[] { root?.Attribute("Sdk")?.Value }
            .Concat(document.Descendants().Where(e => e.Name.LocalName == "Sdk").Select(e => e.Attribute("Name")?.Value))
            .Concat(document.Descendants().Where(e => e.Name.LocalName == "Import").Select(e => e.Attribute("Sdk")?.Value))
            .SelectMany(value => (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries))
            .Select(sdk => sdk.Split('/')[0].Trim())
            .Where(sdk => sdk.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var guids = (Last("ProjectTypeGuids") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries).Select(g => g.Trim('{', '}').ToUpperInvariant()).ToArray();
        var referencesTests = document.Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "package" or "Reference")
            .Select(e => (e.Attribute("Include") ?? e.Attribute("id"))?.Value.Split(',')[0].Trim())
            .Any(id => id is not null && TestFrameworks.Contains(id, StringComparer.OrdinalIgnoreCase));
        return new(sdks, Last("OutputType"), Last("IsTestProject"), Last("AzureFunctionsVersion"), guids,
            referencesTests);
    }
}

/// <summary>The kind of a project and the rule that decided it, as written to diagnostics.json.</summary>
public sealed record ProjectClassification(string Path, ProjectType Type, string Rule);

/// <summary>
/// Tells applications, libraries, tests and samples apart from their project files, without compiling. The rules
/// are heuristics, applied in order: Test, Sample, Application, then Library.
/// </summary>
public static class ProjectClassifier
{
    private const string BuildProps = "/Directory.Build.props";
    private const string WebApplicationGuid = "349C5851-65DF-11DA-9384-00065B846F21";
    private const string TestProjectGuid = "3AC096D0-A1C2-E12C-1390-A8335801FDAB";
    private static readonly string[] SampleFolders = ["samples", "sample", "examples", "example", "demo"];
    private static readonly string[] ApplicationSdks =
        ["Microsoft.NET.Sdk.Web", "Microsoft.NET.Sdk.Worker", "Microsoft.NET.Sdk.BlazorWebAssembly"];

    /// <summary>
    /// The kind of <paramref name="projectFile"/>. Its properties come first, then those of the nearest
    /// Directory.Build.props, the one MSBuild imports. <paramref name="files"/> holds the traits of every file read,
    /// null for a file that could not be read: an unreadable project is Unknown.
    /// </summary>
    public static ProjectClassification Classify(string projectFile, IReadOnlyDictionary<string, ProjectTraits?> files)
    {
        if (!files.TryGetValue(projectFile, out var own) || own is null)
            return new(projectFile, ProjectType.Unknown, "The project file could not be read");

        // Git folders are case-sensitive: /src/App/ is not a parent of /src/app/Api.csproj.
        var directory = projectFile[..(projectFile.LastIndexOf('/') + 1)];
        var props = files
            .Where(f => f.Key.EndsWith(BuildProps, StringComparison.OrdinalIgnoreCase) &&
                directory.StartsWith(f.Key[..(f.Key.LastIndexOf('/') + 1)], StringComparison.Ordinal))
            .OrderByDescending(f => f.Key.Length)
            .Select(f => f.Value)
            .FirstOrDefault() ?? ProjectTraits.None;

        // A property of the project, even empty, replaces the one of Directory.Build.props.
        var isTest = own.IsTestProject ?? props.IsTestProject;
        var outputType = own.OutputType ?? props.OutputType;
        var functions = own.AzureFunctionsVersion ?? props.AzureFunctionsVersion;
        if ("true".Equals(isTest, StringComparison.OrdinalIgnoreCase))
            return new(projectFile, ProjectType.Test, "IsTestProject is true");
        // An inherited reference stays a reference whatever IsTestProject says.
        if (own.ReferencesTestFramework || props.ReferencesTestFramework)
            return new(projectFile, ProjectType.Test, "References a test framework");
        if (own.ProjectTypeGuids.Contains(TestProjectGuid))
            return new(projectFile, ProjectType.Test, "Legacy test project (ProjectTypeGuids)");

        var folders = directory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (folders.FirstOrDefault(f => SampleFolders.Contains(f, StringComparer.OrdinalIgnoreCase)) is { } sample)
            return new(projectFile, ProjectType.Sample, $"In the {sample} folder");

        if (outputType is { } output &&
            (output.Equals("Exe", StringComparison.OrdinalIgnoreCase) ||
             output.Equals("WinExe", StringComparison.OrdinalIgnoreCase)))
            return new(projectFile, ProjectType.Application, $"OutputType is {output}");
        if (own.Sdks.FirstOrDefault(s => ApplicationSdks.Contains(s, StringComparer.OrdinalIgnoreCase)) is { } sdk)
            return new(projectFile, ProjectType.Application, $"SDK {sdk}");
        if (!string.IsNullOrEmpty(functions))
            return new(projectFile, ProjectType.Application, "AzureFunctionsVersion is set");
        if (own.ProjectTypeGuids.Contains(WebApplicationGuid))
            return new(projectFile, ProjectType.Application, "Legacy web application (ProjectTypeGuids)");

        return new(projectFile, ProjectType.Library,
            string.IsNullOrEmpty(outputType) ? "No OutputType" : $"OutputType is {outputType}");
    }

    /// <summary>
    /// The project file a reference belongs to: the file itself, or for a packages.config the project file next to
    /// it (the first in ordinal order). None for a file shared by several projects, such as Directory.Build.props.
    /// </summary>
    public static string? ProjectOf(string file, IEnumerable<string> files)
    {
        if (NuGetDependencyParser.IsProjectFile(file)) return file;
        if (!file.EndsWith("/packages.config", StringComparison.OrdinalIgnoreCase)) return null;
        var directory = file[..(file.LastIndexOf('/') + 1)];
        return files.Where(f => NuGetDependencyParser.IsProjectFile(f) &&
                f.StartsWith(directory, StringComparison.Ordinal) && f.LastIndexOf('/') == directory.Length - 1)
            .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
    }
}
