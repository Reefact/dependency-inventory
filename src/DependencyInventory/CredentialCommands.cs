using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DependencyInventory;

public class CredentialSettings : CommandSettings
{
    [CommandOption("--provider <SOURCE>", isRequired: true)]
    [Description("Source of the code: Azure or GitLab.")]
    public SourceProvider Provider { get; init; }

    [CommandOption("--url")]
    [Description("URL of the Azure DevOps project or the GitLab group.")]
    public string? Url { get; init; }
}

/// <summary>Stores the credentials of an origin in the system vault, so scans no longer prompt for them.</summary>
public sealed class LoginCommand : Command<CredentialSettings>
{
    public override int Execute(CommandContext context, CredentialSettings settings, CancellationToken cancellationToken)
    {
        if (CredentialCommand.Resolve(settings) is not var (store, key)) return 2;

        var credentials = CredentialPrompt.Ask(settings.Provider);
        if (credentials is null)
        {
            ScanConsole.Error("Credentials are required.");
            return 2;
        }
        store.Write(key, credentials);
        AnsiConsole.MarkupLine($"Credentials stored in the {Markup.Escape(store.Name)} for [blue]{Markup.Escape(key)}[/].");
        return 0;
    }
}

/// <summary>Deletes the credentials of an origin from the system vault.</summary>
public sealed class LogoutCommand : Command<CredentialSettings>
{
    public override int Execute(CommandContext context, CredentialSettings settings, CancellationToken cancellationToken)
    {
        if (CredentialCommand.Resolve(settings) is not var (store, key)) return 2;

        AnsiConsole.MarkupLine(store.Delete(key)
            ? $"Credentials deleted from the {Markup.Escape(store.Name)} for [blue]{Markup.Escape(key)}[/]."
            : $"No stored credentials for [blue]{Markup.Escape(key)}[/].");
        return 0;
    }
}

internal static class CredentialCommand
{
    /// <summary>The vault and the entry key, or null after reporting why they are not available.</summary>
    public static (ICredentialStore Store, string Key)? Resolve(CredentialSettings settings)
    {
        var store = CredentialStores.ForCurrentPlatform();
        if (store is null)
        {
            ScanConsole.Error("Storing credentials is not supported yet on this system.");
            return null;
        }

        var url = settings.Url ?? AnsiConsole.Ask<string>("[yellow]Source URL[/]:");
        try
        {
            return (store, CredentialStores.KeyFor(settings.Provider, url));
        }
        catch (ArgumentException ex)
        {
            ScanConsole.Error(ex.Message);
            return null;
        }
    }
}

internal static class CredentialPrompt
{
    /// <summary>Asks for the login (Azure DevOps only) and the PAT; null when one of them is empty.</summary>
    public static RepositoryCredentials? Ask(SourceProvider provider)
    {
        var login = provider == SourceProvider.Azure
            ? AnsiConsole.Ask<string>("[yellow]Azure DevOps login[/]:").Trim()
            : "";
        var pat = AnsiConsole.Prompt(new TextPrompt<string>("[yellow]Platform PAT[/]:").Secret());
        return (provider == SourceProvider.Azure && string.IsNullOrWhiteSpace(login)) ||
            string.IsNullOrWhiteSpace(pat)
                ? null
                : new RepositoryCredentials(login, pat);
    }
}
