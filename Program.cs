using WorkerService1;
using WorkerService1.Config;
using WorkerService1.OfflineQueue;
using WorkerService1.Sync;
using WorkerService1.Terminal;

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

// Swap this one line for ZkTecoTerminalAdapter once real hardware exists —
// nothing else in the app needs to change.
builder.Services.AddSingleton<ITerminalAdapter, MockTerminalAdapter>();

// Typed HttpClient — AddHttpClient<T> gives us pooled connections and
// integrates cleanly with IOptions<AgentSettings> via constructor injection.
builder.Services.AddHttpClient<BackendApiClient>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();