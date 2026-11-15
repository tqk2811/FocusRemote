using System.Windows;
using System.Windows.Threading;
using FocusRemote.Host.Core;
using FocusRemote.Host.Core.Logging;

namespace FocusRemote.Host;

/// <summary>Entry point: installs the three error-catching nets, then shows the main window.</summary>
public partial class App : Application
{
    private readonly FileAppLog _log = new(AppPaths.LogDirectory);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Net 1: any thread. Cannot be handled; the process is going down, so log synchronously.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log.Error($"AppDomain.UnhandledException (terminating={args.IsTerminating})", args.ExceptionObject as Exception);
        // Net 2: UI thread. Handled so the app keeps running.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // Net 3: faulted tasks nobody awaited, raised on GC finalization.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Error("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
        try
        {
            _log.Info($"FocusRemote Host starting, args: {string.Join(' ', e.Args)}");
            bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
            var window = new MainWindow(_log, minimized);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            _log.Error("Startup failed", ex);
            MessageBox.Show("FocusRemote không khởi động được: " + ex.Message, "FocusRemote", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log.Info($"FocusRemote Host exit code {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.Error("Dispatcher.UnhandledException", e.Exception);
        e.Handled = true;
        MessageBox.Show("Lỗi không mong muốn: " + e.Exception.Message + "\nChi tiết trong thư mục log.", "FocusRemote", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
