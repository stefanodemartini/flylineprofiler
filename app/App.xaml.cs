using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ControlzEx.Theming;

namespace DiametroLineaDesktop;

public partial class App : Application
{
    public App()
    {
        // Apply Office-style blue ribbon theme
        ThemeManager.Current.ChangeTheme(this, "Light.Blue");
        SetupGlobalExceptionHandling();
    }

    private void SetupGlobalExceptionHandling()
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            ShowException(args.ExceptionObject as Exception, "AppDomain.CurrentDomain.UnhandledException");
        };

        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            ShowException(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };

        DispatcherUnhandledException += (sender, args) =>
        {
            ShowException(args.Exception, "Application.DispatcherUnhandledException");
            args.Handled = true;
        };
    }

    private void ShowException(Exception? ex, string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sorgente: " + source);
        sb.AppendLine();
        sb.AppendLine(ex?.ToString() ?? "Eccezione nulla");

        try
        {
            MessageBox.Show(
                sb.ToString(),
                "Errore non gestito",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
        }
    }
}