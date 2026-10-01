using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PromptSaver.Desktop.Services;

namespace PromptSaver.Desktop;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = DesktopComposition.CreateServiceProvider();
        MainWindow window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
