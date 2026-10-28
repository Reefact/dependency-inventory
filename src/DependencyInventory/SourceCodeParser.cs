using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyInventory;

/// <summary>
/// The names a C# file references, read from its syntax alone: no compilation, no semantic model.
/// Qualified names are dotted (A.B.C) without type arguments nor "global::".
/// </summary>
public sealed record SourceSymbols(
    IReadOnlySet<string> Imports,
    IReadOnlySet<string> GlobalImports,
    IReadOnlySet<string> GlobalQualifiedNames,
    IReadOnlySet<string> Names,
    IReadOnlySet<string> QualifiedNames,
    IReadOnlySet<string> InvokedMembers);

/// <summary>What a project file or a Directory.Build.props adds to every source file of a project.</summary>
public sealed record ProjectImports(
    string? Sdk,
    bool? ImplicitUsings,
    IReadOnlyList<string> Usings,
    IReadOnlyList<string> QualifiedUsings)
{
    public static readonly ProjectImports None = new(null, null, [], []);

    // The implicit global usings of the .NET SDKs (ImplicitUsings), as of .NET 6 and later.
    private static readonly string[] BaseUsings =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading",
        "System.Threading.Tasks"
    ];
    private static readonly string[] HostingUsings =
    [
        "Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging"
    ];
    private static readonly string[] WebUsings =
    [
        "System.Net.Http.Json", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Routing", .. HostingUsings
    ];

    /// <summary>
    /// Reads the Sdk of the project, ImplicitUsings and the Using items: Include alone imports a namespace,
    /// with Static or Alias it names a type. Conditions are not evaluated.
    /// </summary>
    public static ProjectImports Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        if (root is null) return None;
        var sdk = (string?)root.Attribute("Sdk") ??
            root.Elements().FirstOrDefault(e => e.Name.LocalName == "Sdk")?.Attribute("Name")?.Value;

        bool? implicitUsings = null;
        var usings = new List<string>();
        var qualified = new List<string>();
        foreach (var element in root.Descendants())
        {
            if (element.Name.LocalName == "ImplicitUsings")
                implicitUsings = element.Value.Trim().ToLowerInvariant() is "enable" or "true";
            else if (element.Name.LocalName == "Using" && element.Parent?.Name.LocalName == "ItemGroup" &&
                ((string?)element.Attribute("Include"))?.Trim() is { Length: > 0 } include && !include.Contains('$'))
            {
                var isType = string.Equals((string?)element.Attribute("Static"), "true", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrWhiteSpace((string?)element.Attribute("Alias"));
                (isType ? qualified : usings).Add(include.Replace("global::", ""));
            }
        }
        return new ProjectImports(sdk?.Trim(), implicitUsings, usings, qualified);
    }

    /// <summary>The namespaces imported in every file of a project: its Using items and the SDK's implicit usings.</summary>
    public IEnumerable<string> Namespaces => ImplicitUsings == true
        ? Usings.Concat(Sdk?.Split('/')[0].Trim() switch
        {
            "Microsoft.NET.Sdk.Web" => [.. BaseUsings, .. WebUsings],
            "Microsoft.NET.Sdk.Worker" => [.. BaseUsings, .. HostingUsings],
            _ => BaseUsings
        })
        : Usings;

    /// <summary>
    /// The imports of a project: its own declarations, with what the nearest Directory.Build.props declares.
    /// The project's ImplicitUsings and Sdk win over those of the props file.
    /// </summary>
    public ProjectImports With(ProjectImports? buildProps) => buildProps is null ? this : new ProjectImports(
        Sdk ?? buildProps.Sdk, ImplicitUsings ?? buildProps.ImplicitUsings,
        [.. buildProps.Usings, .. Usings], [.. buildProps.QualifiedUsings, .. QualifiedUsings]);
}

public static class SourceCodeParser
{
    private static readonly CSharpParseOptions Options = new(LanguageVersion.Preview);

    public static bool IsSourceFile(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
        !NuGetDependencyParser.IsInExcludedFolder(path.Replace('\\', '/'));

    public static SourceSymbols Parse(string path, string text)
    {
        var root = CSharpSyntaxTree.ParseText(text, Options, path).GetRoot();
        var imports = new HashSet<string>(StringComparer.Ordinal);
        var globalImports = new HashSet<string>(StringComparer.Ordinal);
        var globalQualified = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var qualified = new HashSet<string>(StringComparer.Ordinal);
        var invoked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case UsingDirectiveSyntax directive when Dotted(directive.NamespaceOrType) is { } target:
                    // A plain using imports a namespace; using static and aliases name a type.
                    var isGlobal = directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword);
                    var isType = directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) || directive.Alias is not null;
                    (isType ? isGlobal ? globalQualified : qualified : isGlobal ? globalImports : imports).Add(target);
                    break;
                case BaseNamespaceDeclarationSyntax declaration when Dotted(declaration.Name) is { } ns:
                    // Code in namespace A.B sees the types of A.B and of A.
                    for (var scope = ns; scope.Length > 0; scope = scope.Contains('.') ? scope[..scope.LastIndexOf('.')] : "")
                        imports.Add(scope);
                    break;
                case SimpleNameSyntax simple when !IsInDeclarationName(simple):
                    names.Add(simple.Identifier.ValueText);
                    break;
                case QualifiedNameSyntax or AliasQualifiedNameSyntax or MemberAccessExpressionSyntax
                    when !IsInDeclarationName(node) && Dotted(node) is { } dotted:
                    qualified.Add(dotted);
                    break;
            }

            if (node is AttributeSyntax attribute)
            {
                // [Fact] is FactAttribute: both spellings are kept.
                if (Dotted(attribute.Name) is { } attributeName) qualified.Add(attributeName + "Attribute");
                names.Add(RightmostName(attribute.Name) + "Attribute");
            }
            else if (node is InvocationExpressionSyntax invocation)
            {
                var member = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                    MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                    _ => null
                };
                if (member is not null) invoked.Add(member);
            }
        }
        return new SourceSymbols(imports, globalImports, globalQualified, names, qualified, invoked);
    }

    // Names in using directives and namespace declarations are imports, not references.
    private static bool IsInDeclarationName(SyntaxNode node) =>
        node.Ancestors().Any(a => a is UsingDirectiveSyntax ||
            a is BaseNamespaceDeclarationSyntax ns && ns.Name.FullSpan.Contains(node.Span));

    private static string RightmostName(NameSyntax name) => name switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => name.ToString()
    };

    /// <summary>A.B.C for a name or a member access made of names only; null otherwise.</summary>
    private static string? Dotted(SyntaxNode? node) => node switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax q => Join(Dotted(q.Left), q.Right.Identifier.ValueText),
        AliasQualifiedNameSyntax { Alias.Identifier.ValueText: "global" } a => a.Name.Identifier.ValueText,
        MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression } m =>
            Join(Dotted(m.Expression), m.Name.Identifier.ValueText),
        _ => null
    };

    private static string? Join(string? left, string right) => left is null ? null : $"{left}.{right}";
}
