using Spectre.Console.Cli;

namespace DependencyInventory;

/// <summary>The depinv commands; every mode is a subcommand, there is no default command.</summary>
public static class DepinvApp
{
    public static CommandApp Create()
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("depinv");
            config.SetApplicationVersion(AppVersion.Current);
            config.AddCommand<PackagesCommand>("packages")
                .WithAlias("pkg")
                .WithDescription("Find the NuGet packages matching --pattern or --regex in every repository.");
            config.AddCommand<FrameworksCommand>("frameworks")
                .WithAlias("fwk")
                .WithDescription("List the target frameworks of every .NET project in every repository.");
            config.AddCommand<LoginCommand>("login")
                .WithDescription("Store the credentials of an origin in the system vault (Windows only for now).");
            config.AddCommand<LogoutCommand>("logout")
                .WithDescription("Delete the credentials of an origin from the system vault.");
        });
        return app;
    }
}
