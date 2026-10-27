using DependencyInventory;

var app = DepinvApp.Create();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
return await app.RunAsync(args, cancellation.Token);
