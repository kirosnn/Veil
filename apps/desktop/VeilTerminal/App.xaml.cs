using Microsoft.UI.Xaml;
using Veil.Diagnostics;

namespace VeilTerminal;

public partial class App : Application
{
    private TerminalMainWindow? _mainWindow;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            AppLogger.Error("Unhandled exception in VeilTerminal.", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new TerminalMainWindow();
        _mainWindow.Activate();
    }
}
