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
    public DateTimeOffset? CommitDate { get; set; }
    public List<string> AvailableBranches { get; } = [];
    public List<FileDiagnostic> Files { get; } = [];

    /// <summary>How the usage of every reference was decided: where its package was read, why it is undetermined.</summary>
    public List<UsageDiagnostic> Usages { get; } = [];

    /// <summary>With --transitive, how the graph of every project and framework was found, and what it could not follow.</summary>
    public List<TransitiveDiagnostic> Transitive { get; } = [];

    /// <summary>The kind of every project file and the rule that decided it.</summary>
    public List<ProjectClassification> Projects { get; } = [];

    /// <summary>The same diagnostic, for another branch on the same commit.</summary>
    public RepositoryDiagnostic OnBranch(string branch)
    {
        var copy = new RepositoryDiagnostic
        {
            Project = Project, Repository = Repository, SelectedBranch = branch, Commit = Commit,
            CommitDate = CommitDate
        };
        copy.AvailableBranches.AddRange(AvailableBranches);
        copy.Files.AddRange(Files);
        copy.Usages.AddRange(Usages);
        copy.Transitive.AddRange(Transitive);
        copy.Projects.AddRange(Projects);
        return copy;
    }
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
    public DateTimeOffset? CommitDate { get; set; }
    public List<string> AvailableBranches { get; } = [];
    public List<FrameworkFileDiagnostic> Files { get; } = [];

    /// <summary>The kind of every project file and the rule that decided it.</summary>
    public List<ProjectClassification> Projects { get; } = [];

    /// <summary>The same diagnostic, for another branch on the same commit.</summary>
    public FrameworkRepositoryDiagnostic OnBranch(string branch)
    {
        var copy = new FrameworkRepositoryDiagnostic
        {
            Project = Project, Repository = Repository, SelectedBranch = branch, Commit = Commit,
            CommitDate = CommitDate
        };
        copy.AvailableBranches.AddRange(AvailableBranches);
        copy.Files.AddRange(Files);
        copy.Projects.AddRange(Projects);
        return copy;
    }
}

public sealed record FrameworkFileDiagnostic(
    string Path,
    int? ContentLength,
    DeclaredFrameworks? Declared,
    string? Error);
