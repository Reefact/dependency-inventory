using Spectre.Console.Cli;
using DependencyInventory;

var app = new CommandApp<ScanCommand>();
app.Configure(config =>
{
    config.SetApplicationName("dependency-inventory");
    config.SetApplicationVersion("1.0.0");
    config.AddCommand<LoginCommand>("login")
        .WithDescription("Store the credentials of an origin in the system vault (Windows only for now).");
    config.AddCommand<LogoutCommand>("logout")
        .WithDescription("Delete the credentials of an origin from the system vault.");
});

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
return await app.RunAsync(args, cancellation.Token);
