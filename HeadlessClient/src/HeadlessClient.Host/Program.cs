using HeadlessClient.Application;
using HeadlessClient.Infrastructure;
using HeadlessClient.Infrastructure.Config;
using HeadlessClient.Infrastructure.Fleet;
using HeadlessClient.Infrastructure.Logging;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Host.UseWindowsService(o => o.ServiceName = "AscensionGm.Headless");
builder.Services.Configure<HostOptions>(o =>
{
    o.ShutdownTimeout = TimeSpan.FromSeconds(45);
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.Services.AddHeadlessApplication();
builder.Services.AddHeadlessInfrastructure(builder.Configuration);

builder.WebHost.UseUrls("http://127.0.0.1:5100");

var app = builder.Build();

app.MapGet("/health", (HeadlessOptions opts, AccountFleetService fleet) =>
    Results.Json(new
    {
        ok = true,
        product = "ascension-gm-headless",
        fleet = new
        {
            runners = fleet.Runners.Count,
            inWorld = fleet.Runners.Count(r => r.IsInWorld),
            autoReconnect = opts.Fleet.AutoReconnect
        }
    }));

Console.WriteLine("[headless] local login host — config from appsettings / appsettings.Local.json");
try
{
    await app.RunAsync().ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    Console.WriteLine("[headless] stopped");
}
finally
{
    try { app.Services.GetService<PacketWireLogger>()?.Dispose(); } catch { /* ignore */ }
}
