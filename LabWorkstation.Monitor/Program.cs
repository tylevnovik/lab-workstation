using LabWorkstation.Monitor;
using LabWorkstation.Monitor.Metrics;

var builder = Host.CreateDefaultBuilder(args);

builder.UseWindowsService();

builder.ConfigureServices(services =>
{
    services.AddSingleton<MonitorLogger>();
    services.AddSingleton(new AlertCooldown(TimeSpan.FromMinutes(10)));
    services.AddSingleton<GpuMetricCollector>();
    services.AddHostedService<MonitorWorker>();
});

var host = builder.Build();
host.Run();
