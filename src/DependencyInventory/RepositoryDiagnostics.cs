namespace DependencyInventory;

/// <summary>
/// Everything examined in one repository, written to diagnostics.json with --diagnostics.
/// Property names are part of that file's format.
/// </summary>
public sealed class RepositoryDiagnostic
{
    public string Project { get; init; } = "";
    public string Repository { get; init; } = "";
    public string? SelectedBranch { get; set; }
    public string? Commit { get; set; }
    public List<string> AvailableBranches { get; } = [];
    public List<FileDiagnostic> Files { get; } = [];
}

public sealed record FileDiagnostic(
    string Path,
    int? ContentLength,
    IReadOnlyList<ParsedPackage> Packages,
    string? Error);

/// <summary>
/// Everything examined in one repository by the frameworks inventory, written to diagnostics.json with
/// --diagnostics. Property names are part of that file's format.
/// </summary>
public sealed class FrameworkRepositoryDiagnostic
{
    public string Project { get; init; } = "";
    public string Repository { get; init; } = "";
    public string? SelectedBranch { get; set; }
    public string? Commit { get; set; }
    public List<string> AvailableBranches { get; } = [];
    public List<FrameworkFileDiagnostic> Files { get; } = [];
}

public sealed record FrameworkFileDiagnostic(
    string Path,
    int? ContentLength,
    DeclaredFrameworks? Declared,
    string? Error);
