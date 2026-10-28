using Xunit;

namespace DependencyInventory.Tests;

public sealed class ProjectTypeTests
{
    private static ProjectClassification Classify(string path, string project,
        params (string Path, string Xml)[] others)
    {
        var files = new Dictionary<string, ProjectTraits?>(StringComparer.Ordinal)
        {
            [path] = ProjectTraits.Parse(project)
        };
        foreach (var (otherPath, xml) in others) files[otherPath] = ProjectTraits.Parse(xml);
        return ProjectClassifier.Classify(path, files);
    }

    [Theory]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>""",
        "Test", "IsTestProject is true")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" /></ItemGroup></Project>""",
        "Test", "References a test framework")]
    [InlineData("""<Project><ItemGroup><Reference Include="nunit.framework, Version=3.0.0.0" /></ItemGroup></Project>""",
        "Test", "References a test framework")]
    [InlineData("""<Project><PropertyGroup><ProjectTypeGuids>{3AC096D0-A1C2-E12C-1390-A8335801FDAB};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}</ProjectTypeGuids></PropertyGroup></Project>""",
        "Test", "Legacy test project (ProjectTypeGuids)")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""",
        "Application", "OutputType is Exe")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>""",
        "Application", "OutputType is WinExe")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Web" />""", "Application", "SDK Microsoft.NET.Sdk.Web")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Worker" />""", "Application", "SDK Microsoft.NET.Sdk.Worker")]
    [InlineData("""<Project><Sdk Name="Microsoft.NET.Sdk.Web" /></Project>""", "Application", "SDK Microsoft.NET.Sdk.Web")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><AzureFunctionsVersion>v4</AzureFunctionsVersion></PropertyGroup></Project>""",
        "Application", "AzureFunctionsVersion is set")]
    [InlineData("""<Project><PropertyGroup><OutputType>Library</OutputType><ProjectTypeGuids>{349c5851-65df-11da-9384-00065b846f21};{fae04ec0-301f-11d3-bf4b-00c04f79efbc}</ProjectTypeGuids></PropertyGroup></Project>""",
        "Application", "Legacy web application (ProjectTypeGuids)")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>""",
        "Library", "OutputType is Library")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk" />""", "Library", "No OutputType")]
    public void EachRuleClassifiesAProject(string project, string type, string rule)
    {
        var classification = Classify("/src/Api/Api.csproj", project);

        Assert.Equal((Enum.Parse<ProjectType>(type), rule), (classification.Type, classification.Rule));
    }

    [Fact]
    public void ATestProjectThatIsAnExecutableIsATest() =>
        Assert.Equal(ProjectType.Test, Classify("/tests/Api.Tests.csproj",
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><IsTestProject>true</IsTestProject></PropertyGroup></Project>""").Type);

    [Theory]
    [InlineData("/samples/Web/Web.csproj", "In the samples folder")]
    [InlineData("/src/Examples/Console/Console.csproj", "In the Examples folder")]
    [InlineData("/demo/Api.csproj", "In the demo folder")]
    public void AnApplicationInASampleFolderIsASample(string path, string rule) =>
        Assert.Equal((ProjectType.Sample, rule),
            (Classify(path, """<Project Sdk="Microsoft.NET.Sdk.Web" />""").Type, Classify(path, """<Project Sdk="Microsoft.NET.Sdk.Web" />""").Rule));

    [Fact]
    public void ATestInASampleFolderStaysATest() =>
        Assert.Equal(ProjectType.Test, Classify("/samples/Web.Tests/Web.Tests.csproj",
            """<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>""").Type);

    [Fact]
    public void ThePropertiesOfTheNearestDirectoryBuildPropsApplyUnlessTheProjectOverridesThem()
    {
        var props = ("/tests/Directory.Build.props",
            """<Project><PropertyGroup><IsTestProject>true</IsTestProject><OutputType>Exe</OutputType></PropertyGroup></Project>""");
        var farther = ("/Directory.Build.props",
            """<Project><PropertyGroup><AzureFunctionsVersion>v4</AzureFunctionsVersion></PropertyGroup></Project>""");

        Assert.Equal(ProjectType.Test, Classify("/tests/Unit/Unit.csproj", "<Project />", props, farther).Type);
        Assert.Equal((ProjectType.Application, "OutputType is Exe"), Rule(Classify("/tests/Tool/Tool.csproj",
            "<Project><PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup></Project>", props, farther)));
        // Only the nearest Directory.Build.props is imported: the farther one does not make a function app.
        Assert.Equal(ProjectType.Library,
            Classify("/tests/Lib/Lib.csproj", "<Project><PropertyGroup><IsTestProject>false</IsTestProject><OutputType>Library</OutputType></PropertyGroup></Project>",
                props, farther).Type);
        Assert.Equal(ProjectType.Application, Classify("/src/Func/Func.csproj", "<Project />", props, farther).Type);
    }

    [Fact]
    public void AnUnreadableProjectIsUnknown() =>
        Assert.Equal(ProjectType.Unknown, ProjectClassifier.Classify("/src/Api.csproj",
            new Dictionary<string, ProjectTraits?> { ["/src/Api.csproj"] = null }).Type);

    [Fact]
    public void AReferenceBelongsToItsProjectOrToNone()
    {
        string[] files = ["/src/Web/Web.csproj", "/src/Web/packages.config", "/src/Web/Sub/Sub.csproj",
            "/Directory.Build.props", "/src/Other/packages.config"];

        Assert.Equal("/src/Web/Web.csproj", ProjectClassifier.ProjectOf("/src/Web/Web.csproj", files));
        Assert.Equal("/src/Web/Web.csproj", ProjectClassifier.ProjectOf("/src/Web/packages.config", files));
        Assert.Null(ProjectClassifier.ProjectOf("/src/Other/packages.config", files));
        Assert.Null(ProjectClassifier.ProjectOf("/Directory.Build.props", files));
    }

    [Fact]
    public async Task TheScanGivesEveryReferenceTheTypeOfItsProject()
    {
        const string serilog = """<PackageReference Include="Serilog" Version="3.0.0" />""";
        var api = new FakeRepositoryProvider().AddRepository("api", files: new Dictionary<string, string>
        {
            ["/Directory.Build.props"] = $"<Project><ItemGroup>{serilog}</ItemGroup></Project>",
            ["/src/Api/Api.csproj"] = $"""<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup>{serilog}</ItemGroup></Project>""",
            ["/tests/Api.Tests/Api.Tests.csproj"] =
                $"""<Project><ItemGroup><PackageReference Include="xunit" Version="2.9.3" />{serilog}</ItemGroup></Project>""",
            ["/legacy/Site/Site.csproj"] =
                "<Project><PropertyGroup><ProjectTypeGuids>{349C5851-65DF-11DA-9384-00065B846F21}</ProjectTypeGuids></PropertyGroup></Project>",
            ["/legacy/Site/packages.config"] = """<packages><package id="Serilog" version="2.0.0" /></packages>"""
        });
        var diagnostic = new RepositoryDiagnostic { Project = "project", Repository = "api" };

        var result = await RepositoryScanner.ScanRepositoryAsync(api, api.Repositories[0],
            DependencyMatcher.Create(["Serilog"], null), diagnostic, CancellationToken.None);

        Assert.Equal(["/Directory.Build.props -", "/legacy/Site/packages.config Application", "/src/Api/Api.csproj Application",
                "/tests/Api.Tests/Api.Tests.csproj Test"],
            result.Occurrences.Select(o => $"{o.File} {o.ProjectType?.ToString() ?? "-"}").Order(StringComparer.Ordinal));
        Assert.Equal(["/legacy/Site/Site.csproj Application", "/src/Api/Api.csproj Application",
                "/tests/Api.Tests/Api.Tests.csproj Test"],
            diagnostic.Projects.Select(p => $"{p.Path} {p.Type}"));
    }

    [Fact]
    public async Task TheFrameworksInventoryGivesEveryProjectItsType()
    {
        var api = new FakeRepositoryProvider().AddRepository("api", files: new Dictionary<string, string>
        {
            ["/src/Api/Api.csproj"] =
                """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""",
            ["/src/Lib/Lib.csproj"] =
                "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
            ["/src/Broken/Broken.csproj"] = "<not xml"
        });

        var result = await FrameworkScanner.ScanAsync(api, api.Repositories, true, ScanParallelism.Sequential, null,
            CancellationToken.None);

        Assert.Equal(["/src/Api/Api.csproj Application", "/src/Broken/Broken.csproj Unknown", "/src/Lib/Lib.csproj Library"],
            result.Frameworks.Select(f => $"{f.ProjectFile} {f.ProjectType}"));
        Assert.Equal(3, Assert.Single(result.Diagnostics).Projects.Count);
    }

    private static (ProjectType, string) Rule(ProjectClassification classification) =>
        (classification.Type, classification.Rule);
}
