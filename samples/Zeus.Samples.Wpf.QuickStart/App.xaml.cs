using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Zeus;

namespace Zeus.Samples.Wpf.QuickStart;

/// <summary>
/// WPF 示例入口。这里作为组合根，集中登记宿主、Dispatcher、ViewModel 和窗口。
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = ConfigureServices();
        MainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IUiDispatcher>(_ => WpfUiDispatcher.Current());
        services.AddSingleton<IZeusHost>(_ => ZeusHost.Create(builder => builder.AddVirtualChannel("meter")));
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();
    }
}
