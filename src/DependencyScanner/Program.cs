using Spectre.Console.Cli;
using DependencyScanner;

var app = new CommandApp<ScanCommand>();
app.Configure(config =>
{
    config.SetApplicationName("dependency-scan");
    config.SetApplicationVersion("1.0.0");
});

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
return await app.RunAsync(args, cancellation.Token);
