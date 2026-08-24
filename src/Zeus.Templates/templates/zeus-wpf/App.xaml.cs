using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Zeus;

namespace Zeus.WpfTemplate;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = ConfigureServices();
        MainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IUiDispatcher>(_ => WpfUiDispatcher.Current());
        services.AddSingleton<IZeusHost>(_ => ZeusHost.Create(builder =>
        {
            builder.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "zeus.json"));
        }));
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();
    }
}
