using WorkerService1;
using WorkerService1.Config;
using WorkerService1.OfflineQueue;
using WorkerService1.Sync;
using WorkerService1.Terminal;
using Microsoft.Extensions.Logging;

if (args.Length >= 2 && args[0] == "--enroll")
{
    var terminalUserId = args[1];
    var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var logger = loggerFactory.CreateLogger<SecuGenTerminalAdapter>();
    var adapter = new SecuGenTerminalAdapter(logger);
    await adapter.ConnectAsync(CancellationToken.None);
    var success = await adapter.EnrollAsync(terminalUserId, CancellationToken.None);
    Console.WriteLine(success ? "Enrollment succeeded." : "Enrollment failed.");
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// Lets this run as a real Windows Service later via `sc create` / installer,
// while still running as a normal console app during development (dotnet run).
builder.Services.AddWindowsService();

// Binds the "Agent" section of appsettings.json to AgentSettings, so
// BackendApiClient (and anything else) can inject IOptions<AgentSettings>
// instead of reading configuration strings directly.
builder.Services.Configure<AgentSettings>(builder.Configuration.GetSection("Agent"));

// Singleton: one shared SQLite-backed queue for the whole agent's lifetime.
builder.Services.AddSingleton<OfflineQueueStore>();

// Real SecuGen hardware adapter — swap back to MockTerminalAdapter if you
// need to test without the device plugged in.
builder.Services.AddSingleton<ITerminalAdapter, SecuGenTerminalAdapter>();

// Typed HttpClient — AddHttpClient<T> gives us pooled connections and
// integrates cleanly with IOptions<AgentSettings> via constructor injection.
builder.Services.AddHttpClient<BackendApiClient>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();