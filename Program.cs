using WorkerService1;
using WorkerService1.Config;
using WorkerService1.OfflineQueue;
using WorkerService1.Sync;
using WorkerService1.Terminal;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddWindowsService();
builder.Services.Configure<AgentSettings>(builder.Configuration.GetSection("Agent"));
builder.Services.AddSingleton<OfflineQueueStore>();
builder.Services.AddSingleton<ITerminalAdapter, MockTerminalAdapter>();
builder.Services.AddHttpClient<BackendApiClient>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();