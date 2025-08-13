using EchoBot;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Logging.EventLog;
using System.Runtime.InteropServices;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "Echo Bot Service";
    })
    .ConfigureServices(services =>
    {
        // Only register EventLog provider on Windows platform
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            LoggerProviderOptions.RegisterProviderOptions<
                EventLogSettings, EventLogLoggerProvider>(services);
        }

        services.AddSingleton<IBotHost, BotHost>();

        services.AddHostedService<EchoBotWorker>();
    })
    .Build();

await host.RunAsync();
