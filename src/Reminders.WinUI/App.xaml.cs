using Microsoft.UI.Xaml;

namespace Reminders.Windows;

public partial class App : Application
{
    private Window? _window;
    public App()
    {
        UnhandledException += (_, args) => Services.AppLog.Error("Unhandled UI exception", args.Exception);
        Services.AppLog.Info("Starting Reminders " + typeof(App).Assembly.GetName().Version + " build=" +
            System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion);
        try { InitializeComponent(); }
        catch (Exception error)
        {
            Services.AppLog.Error("Application resources could not be initialized", error);
            throw;
        }
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
#if DEBUG
            if (Environment.GetCommandLineArgs().Contains("--test-apple-sign-in", StringComparer.OrdinalIgnoreCase))
                _window.Title = "Reminders for Windows · Sign-in test";
#endif
            _window.Activate();
            Services.AppLog.Info("Main window activated");
        }
        catch (Exception error)
        {
            Services.AppLog.Error("Main window could not be launched", error);
            throw;
        }
    }
}
