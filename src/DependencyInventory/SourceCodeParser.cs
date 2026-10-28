using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DependencyInventory;

/// <summary>
/// The names a C# file references, read from its syntax alone: no compilation, no semantic model.
/// Qualified names are dotted (A.B.C) without type arguments nor "global::". <see cref="Conditional"/> holds the
/// names of the #if branches whose activation depends on symbols, which are unknown: they may or may not be compiled.
/// </summary>
public sealed record SourceSymbols(
    IReadOnlySet<string> Imports,
    IReadOnlySet<string> GlobalImports,
    IReadOnlySet<string> GlobalQualifiedNames,
    IReadOnlySet<string> Names,
    IReadOnlySet<string> QualifiedNames,
    IReadOnlySet<string> InvokedMembers,
    SourceSymbols? Conditional = null)
{
    /// <summary>The names of both, as if every conditional branch were compiled.</summary>
    public SourceSymbols WithConditional() => Conditional is not { } other ? this : new SourceSymbols(
        Union(Imports, other.Imports), Union(GlobalImports, other.GlobalImports),
        Union(GlobalQualifiedNames, other.GlobalQualifiedNames), Union(Names, other.Names),
        Union(QualifiedNames, other.QualifiedNames), Union(InvokedMembers, other.InvokedMembers));

    private static HashSet<string> Union(IReadOnlySet<string> first, IReadOnlySet<string> second) =>
        new(first.Concat(second), StringComparer.Ordinal);
}

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

    // Disabled code holds nested #if blocks of its own: they are read this deep.
    private const int MaxDisabledDepth = 3;

    /// <summary>
    /// Reads the names of a file. Code certainly compiled (outside #if, or in #if true) gives the names; code whose
    /// compilation depends on symbols, active or disabled in this parse, gives the conditional names; code never
    /// compiled (#if false) is ignored.
    /// </summary>
    public static SourceSymbols Parse(string path, string text)
    {
        // An alias declared outside #if applies in conditional code as well, and the other way round.
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var active = new Collector(aliases);
        var conditional = new Collector(aliases);
        Collect(CSharpSyntaxTree.ParseText(text, Options, path).GetRoot(), active, conditional, false, 0);
        return active.ToSymbols(conditional.ToSymbols(null));
    }

    private static void Collect(SyntaxNode root, Collector active, Collector conditional, bool allConditional,
        int depth)
    {
        var branches = ConditionalBranch.Read(root);
        foreach (var node in root.DescendantNodes())
        {
            var activation = allConditional ? Activation.Unknown : ConditionalBranch.At(branches, node.SpanStart);
            if (activation != Activation.Dead) (activation == Activation.Certain ? active : conditional).Add(node);
        }

        if (depth >= MaxDisabledDepth) return;
        foreach (var disabled in root.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.DisabledTextTrivia)))
            if (ConditionalBranch.At(branches, disabled.SpanStart) != Activation.Dead)
                Collect(CSharpSyntaxTree.ParseText(disabled.ToString(), Options).GetRoot(), active, conditional, true,
                    depth + 1);
    }

    private enum Activation { Certain, Unknown, Dead }

    /// <summary>The code between two directives of an #if block, and whether it is compiled.</summary>
    private sealed record ConditionalBranch(TextSpan Span, Activation Activation)
    {
        /// <summary>
        /// The branches of the #if blocks, nested ones included. A condition made of true, false, !, &amp;&amp;, ||,
        /// == and != only is evaluated; any symbol makes it unknown. A branch after one certainly taken is dead.
        /// </summary>
        public static IReadOnlyList<ConditionalBranch> Read(SyntaxNode root)
        {
            var branches = new List<ConditionalBranch>();
            var blocks = new Stack<(Activation Parent, bool Taken, bool MaybeTaken, int Start, Activation Current)>();
            foreach (var directive in root.DescendantTrivia().Where(t => t.IsDirective)
                         .Select(t => t.GetStructure()).OfType<DirectiveTriviaSyntax>())
            {
                var (condition, opens, closes) = directive switch
                {
                    IfDirectiveTriviaSyntax d => (Evaluate(d.Condition), true, false),
                    ElifDirectiveTriviaSyntax d => (Evaluate(d.Condition), false, true),
                    ElseDirectiveTriviaSyntax => (true, false, true),
                    EndIfDirectiveTriviaSyntax => ((bool?)null, false, true),
                    _ => (null, false, false)
                };
                if (!opens && !closes || closes && blocks.Count == 0) continue;
                if (closes)
                {
                    var block = blocks.Pop();
                    branches.Add(new(TextSpan.FromBounds(block.Start, directive.FullSpan.Start), block.Current));
                    if (directive is EndIfDirectiveTriviaSyntax) continue;
                    var activation = block.Taken || condition == false ? Activation.Dead
                        : condition == true && !block.MaybeTaken ? Activation.Certain : Activation.Unknown;
                    blocks.Push((block.Parent, block.Taken || condition == true, block.MaybeTaken || condition != false,
                        directive.FullSpan.End, Combine(block.Parent, activation)));
                }
                else
                {
                    var parent = blocks.Count > 0 ? blocks.Peek().Current : Activation.Certain;
                    var activation = condition switch { true => Activation.Certain, false => Activation.Dead, _ => Activation.Unknown };
                    blocks.Push((parent, condition == true, condition != false, directive.FullSpan.End,
                        Combine(parent, activation)));
                }
            }
            return branches;
        }

        /// <summary>The activation of the innermost branch holding a position; certain outside any #if.</summary>
        public static Activation At(IReadOnlyList<ConditionalBranch> branches, int position) =>
            branches.Where(b => b.Span.Contains(position)).MinBy(b => b.Span.Length)?.Activation ?? Activation.Certain;

        private static Activation Combine(Activation parent, Activation own) => (Activation)Math.Max((int)parent, (int)own);

        private static bool? Evaluate(ExpressionSyntax condition) => condition switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression) => true,
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.FalseLiteralExpression) => false,
            ParenthesizedExpressionSyntax p => Evaluate(p.Expression),
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not => !Evaluate(not.Operand),
            BinaryExpressionSyntax binary => (binary.Kind(), Evaluate(binary.Left), Evaluate(binary.Right)) switch
            {
                (SyntaxKind.LogicalAndExpression, false, _) or (SyntaxKind.LogicalAndExpression, _, false) => false,
                (SyntaxKind.LogicalAndExpression, true, true) => true,
                (SyntaxKind.LogicalOrExpression, true, _) or (SyntaxKind.LogicalOrExpression, _, true) => true,
                (SyntaxKind.LogicalOrExpression, false, false) => false,
                (SyntaxKind.EqualsExpression, { } left, { } right) => left == right,
                (SyntaxKind.NotEqualsExpression, { } left, { } right) => left != right,
                _ => null
            },
            _ => null
        };
    }

    private sealed class Collector(Dictionary<string, string> aliases)
    {
        private readonly HashSet<string> _imports = new(StringComparer.Ordinal);
        private readonly HashSet<string> _globalImports = new(StringComparer.Ordinal);
        private readonly HashSet<string> _globalQualified = new(StringComparer.Ordinal);
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly HashSet<string> _qualified = new(StringComparer.Ordinal);
        private readonly HashSet<string> _invoked = new(StringComparer.Ordinal);

        // using Msg = Contoso.Messaging: Msg.Bus is Contoso.Messaging.Bus.
        private readonly Dictionary<string, string> _aliases = aliases;

        public SourceSymbols ToSymbols(SourceSymbols? conditional) =>
            new(_imports, _globalImports, _globalQualified, _names, _qualified, _invoked, conditional);

        public void Add(SyntaxNode node)
        {
            switch (node)
            {
                case UsingDirectiveSyntax directive when Dotted(directive.NamespaceOrType) is { } target:
                    // A plain using imports a namespace; using static and aliases name a type, or a namespace
                    // for an alias, which is then expanded in the names it qualifies.
                    var isGlobal = directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword);
                    var isType = directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) || directive.Alias is not null;
                    (isType ? isGlobal ? _globalQualified : _qualified : isGlobal ? _globalImports : _imports).Add(target);
                    if (directive.Alias is { } alias) _aliases[alias.Name.Identifier.ValueText] = target;
                    break;
                case BaseNamespaceDeclarationSyntax declaration when FullNamespace(declaration) is { } ns:
                    // Code in namespace A.B sees the types of A.B and of A.
                    for (var scope = ns; scope.Length > 0; scope = scope.Contains('.') ? scope[..scope.LastIndexOf('.')] : "")
                        _imports.Add(scope);
                    break;
                case SimpleNameSyntax simple when !IsInDeclarationName(simple):
                    _names.Add(simple.Identifier.ValueText);
                    break;
                case QualifiedNameSyntax or AliasQualifiedNameSyntax or MemberAccessExpressionSyntax
                    when !IsInDeclarationName(node) && Dotted(node) is { } dotted:
                    AddQualified(dotted);
                    break;
            }

            if (node is AttributeSyntax attribute)
            {
                // [Fact] is FactAttribute: both spellings are kept.
                if (Dotted(attribute.Name) is { } attributeName) AddQualified(attributeName + "Attribute");
                _names.Add(RightmostName(attribute.Name) + "Attribute");
            }
            else if (node is InvocationExpressionSyntax invocation)
            {
                var member = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                    MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                    _ => null
                };
                if (member is not null) _invoked.Add(member);
            }
        }

        private void AddQualified(string dotted)
        {
            _qualified.Add(dotted);
            var first = dotted.IndexOf('.') is var dot and >= 0 ? dotted[..dot] : dotted;
            if (_aliases.TryGetValue(first, out var target)) _qualified.Add(target + dotted[first.Length..]);
        }
    }

    /// <summary>The full name of a namespace, nested in others or not: namespace A { namespace B } is A.B.</summary>
    private static string? FullNamespace(BaseNamespaceDeclarationSyntax declaration)
    {
        var parts = declaration.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>()
            .Select(d => Dotted(d.Name)).Reverse().ToArray();
        return parts.Any(p => p is null) ? null : string.Join('.', parts);
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
        // global::A is A; Msg::Bus is Msg.Bus, which the alias expands.
        AliasQualifiedNameSyntax { Alias.Identifier.ValueText: "global" } a => a.Name.Identifier.ValueText,
        AliasQualifiedNameSyntax a => $"{a.Alias.Identifier.ValueText}.{a.Name.Identifier.ValueText}",
        MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression } m =>
            Join(Dotted(m.Expression), m.Name.Identifier.ValueText),
        _ => null
    };

    private static string? Join(string? left, string right) => left is null ? null : $"{left}.{right}";
}
