using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DependencyInventory;

/// <summary>Options shared by every command that reads an origin: the origin and its credentials.</summary>
public abstract class OriginSettings : CommandSettings
{
    [CommandOption("--provider <SOURCE>", isRequired: true)]
    [Description("Source of the code: Azure or GitLab.")]
    public SourceProvider Provider { get; init; }

    [CommandOption("--url")]
    [Description("URL of the Azure DevOps project or the GitLab group.")]
    public string? Url { get; init; }

    [CommandOption("--credentials-file [PATH]")]
    [Description("Credentials read from a file; .azure-credentials or .gitlab-credentials by default. " +
        "Linux and macOS only: on Windows, use the login command.")]
    public required FlagValue<string?> CredentialsFile { get; init; }
}

/// <summary>Options shared by every repository inventory: the origin and how its repositories are scanned.</summary>
public abstract class RepositorySettings : OriginSettings
{
    [CommandOption("--repository")]
    [Description("Scan a single repository (useful to diagnose a missing result).")]
    public string? Repository { get; init; }

    [CommandOption("--branch <NAME>")]
    [Description("Branch to scan, repeatable in priority order: a name, a glob such as release/*, or @default for " +
        "the repository's default branch. The first that exists is scanned; the default branch without this option.")]
    public string[]? Branches { get; init; }

    [CommandOption("--branch-file <PATH>")]
    [Description("File listing the branches to scan, one per line in priority order; [[repository]] sections give " +
        "repositories their own list.")]
    public string? BranchFile { get; init; }

    [CommandOption("--branch-mode <MODE>")]
    [Description("first (default): the first listed branch that exists; all: every listed branch that exists, " +
        "each on its own lines in the reports. all needs --branch or --branch-file.")]
    [DefaultValue(BranchMode.First)]
    public BranchMode BranchMode { get; init; } = BranchMode.First;

    [CommandOption("--diagnostics")]
    [Description("Export the branches and what was read in every file to diagnostics.json.")]
    public bool Diagnostics { get; init; }

    [CommandOption("--parallelism <N>")]
    [Description("Number of simultaneous requests, from 1 (sequential) to 8.")]
    [DefaultValue(1)]
    public int Parallelism { get; init; } = 1;

    [CommandOption("--parallelism-mode <MODE>")]
    [Description("Parallelism mode (files or repositories); required when --parallelism is greater than 1.")]
    public ParallelismMode? ParallelismMode { get; init; }
}

/// <summary>
/// Validates the shared options, resolves the credentials and selects the repositories, then lets the
/// inventory scan them. Exit codes: 0 complete, 1 incomplete or failed, 2 invalid input, 130 interrupted.
/// </summary>
public abstract class RepositoryCommand<TSettings> : AsyncCommand<TSettings> where TSettings : RepositorySettings
{
    /// <summary>Validates the inventory's own options, before any prompt for credentials; false stops with 2.</summary>
    protected virtual bool Prepare(TSettings settings, string url) => true;

    /// <summary>
    /// Resolves the other credentials the inventory needs, once those of the origin are known; any other result
    /// than 0 stops with that exit code.
    /// </summary>
    protected virtual Task<int> AuthenticateAsync(TSettings settings, string url, RepositoryCredentials credentials,
        CancellationToken cancellationToken) => Task.FromResult(0);

    /// <summary>
    /// Scans the selected repositories on the branches of <paramref name="branches"/>, writes the reports and
    /// returns the exit code.
    /// </summary>
    protected abstract Task<int> InventoryAsync(IRepositoryProvider api, IReadOnlyList<Repository> repositories,
        BranchPolicy branches, ScanParallelism parallelism, TSettings settings, CancellationToken cancellationToken);

    public override async Task<int> ExecuteAsync(
        CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        ScanConsole.Header();

        var url = settings.Url ?? AnsiConsole.Ask<string>("[yellow]Source URL[/]:");
        try { RepositoryProviderFactory.ValidateOrigin(settings.Provider, url); }
        catch (ArgumentException ex)
        {
            ScanConsole.Error(ex.Message);
            return 2;
        }

        if (!Prepare(settings, url)) return 2;

        BranchPolicy branches;
        try { branches = BranchPolicy.FromOptions(settings.Branches, settings.BranchFile, settings.BranchMode); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or
            UnauthorizedAccessException)
        {
            ScanConsole.Error(ex.Message);
            return 2;
        }

        ScanParallelism parallelism;
        try { parallelism = ScanParallelism.Create(settings.Parallelism, settings.ParallelismMode); }
        catch (ArgumentException ex)
        {
            ScanConsole.Error(ex.Message);
            return 2;
        }

        var (credentials, exitCode) = await OriginCredentials.ResolveAsync(settings, url, cancellationToken);
        if (credentials is null) return exitCode;
        exitCode = await AuthenticateAsync(settings, url, credentials, cancellationToken);
        if (exitCode != 0) return exitCode;

        try
        {
            using IRepositoryProvider api = RepositoryProviderFactory.Create(
                settings.Provider, url, credentials, ScanConsole.RetryScheduled);
            var repositories = await api.GetRepositoriesAsync(cancellationToken);
            var selectedRepositories = RepositoryScanner.Select(repositories, settings.Repository);
            if (selectedRepositories.Count == 0)
            {
                ScanConsole.Error($"Repository not found: {settings.Repository ?? ""}");
                return 2;
            }

            ScanConsole.Source(api.Origin, repositories.Count, selectedRepositories.Count);
            ScanConsole.Branches(branches);
            return await InventoryAsync(api, selectedRepositories, branches, parallelism, settings,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ScanConsole.Warning("Scan interrupted.");
            return 130;
        }
        catch (HttpRequestException ex)
        {
            ScanConsole.Error(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            ScanConsole.Error($"Failed: {ex.Message}");
            return 1;
        }
    }
}

/// <summary>Resolves the credentials of an origin for a command, reporting failures on the console.</summary>
internal static class OriginCredentials
{
    /// <summary>The credentials, or none and the exit code to stop with: 2 invalid input, 130 interrupted.</summary>
    public static Task<(RepositoryCredentials? Credentials, int ExitCode)> ResolveAsync(
        OriginSettings settings, string url, CancellationToken cancellationToken) =>
        // Secrets are never logged; Azure uses login and PAT, GitLab the PAT alone.
        ResolveAsync(store => CredentialResolver.ResolveAsync(settings.Provider, url,
            settings.CredentialsFile.IsSet, settings.CredentialsFile.Value,
            store, () => CredentialPrompt.Ask(settings.Provider), cancellationToken), "Credentials are required.");

    /// <summary>
    /// The credentials of another Azure DevOps organization than the origin's, for its feeds: the Azure credentials
    /// file when --credentials-file is given (the default one when the origin is GitLab), then the vault entry of the
    /// organization, then the prompt.
    /// </summary>
    public static Task<(RepositoryCredentials? Credentials, int ExitCode)> ResolveOrganizationAsync(
        OriginSettings settings, string organization, CancellationToken cancellationToken) =>
        ResolveAsync(store => CredentialResolver.ResolveKeyAsync(SourceProvider.Azure,
                () => CredentialStores.AzureKey(organization), settings.CredentialsFile.IsSet,
                settings.Provider == SourceProvider.Azure ? settings.CredentialsFile.Value : null, store,
                () =>
                {
                    ScanConsole.OrganizationCredentials(organization);
                    return CredentialPrompt.Ask(SourceProvider.Azure);
                }, cancellationToken),
            $"Credentials are required for the Azure DevOps organization {organization}.");

    private static async Task<(RepositoryCredentials? Credentials, int ExitCode)> ResolveAsync(
        Func<ICredentialStore?, Task<ResolvedCredentials>> resolve, string missing)
    {
        var store = CredentialStores.ForCurrentPlatform();
        try
        {
            var resolved = await resolve(store);
            if (resolved.StoreUnavailable is not null)
                ScanConsole.Warning(resolved.StoreUnavailable);
            if (resolved.Credentials is null)
            {
                ScanConsole.Error(missing);
                return (null, 2);
            }
            if (resolved.Source == CredentialSource.Store)
                ScanConsole.CredentialsFromStore(store!.Name);
            return (resolved.Credentials, 0);
        }
        catch (NotSupportedException ex)
        {
            ScanConsole.Error(ex.Message);
            return (null, 2);
        }
        catch (OperationCanceledException)
        {
            ScanConsole.Warning("Reading interrupted.");
            return (null, 130);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            FormatException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            ScanConsole.Error($"Cannot read the credentials: {ex.Message}");
            return (null, 2);
        }
    }
}
