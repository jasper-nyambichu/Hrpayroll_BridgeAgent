using System.Windows;
using WorkerService1;
using WorkerService1.Config;
using WorkerService1.OfflineQueue;
using WorkerService1.Sync;
using WorkerService1.Terminal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WorkerService1;

public class Program
{
    public static IHost? AppHost { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Services.Configure<AgentSettings>(builder.Configuration.GetSection("Agent"));
        builder.Services.AddSingleton<OfflineQueueStore>();
        builder.Services.AddSingleton<AttendanceLogStore>();
        builder.Services.AddSingleton<ITerminalAdapter, MockTerminalAdapter>();
        builder.Services.AddHttpClient<BackendApiClient>();
        builder.Services.AddHostedService<Worker>();
        builder.Services.AddSingleton<MainWindow>();

        AppHost = builder.Build();
        AppHost.Start();

        var app = new Application();
        var mainWindow = AppHost.Services.GetRequiredService<MainWindow>();
        app.Run(mainWindow);

        AppHost.StopAsync().GetAwaiter().GetResult();
    }
}