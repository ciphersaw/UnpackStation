using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace UnpackStation;

/// <summary>Application entry point: hooks up global exception handling so unhandled exceptions do not crash silently without a trace.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Unhandled exceptions on the UI thread
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // Unhandled exceptions on background threads
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        // Unobserved exceptions in tasks
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("Dispatcher", e.Exception);
        ShowCrashReport("An unhandled exception occurred; the operation has been aborted.", "Dispatcher", e.Exception);
        // Mark as handled and keep running; the user can copy the diagnostic information and decide whether to close
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception ex)
        {
            return;
        }

        WriteCrashLog("AppDomain", ex);
        // Background threads cannot touch the UI directly; dispatch back to the UI thread to show the report
        try
        {
            Application.Current?.Dispatcher.Invoke(
                () => ShowCrashReport("An unhandled exception occurred in a background task.", "AppDomain", ex));
        }
        catch (Exception)
        {
            // Do not throw again when the UI has already exited; the log on disk is enough
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("Task", e.Exception);
        e.SetObserved();
        // Unobserved task exceptions are mostly finalization notices for completed tasks; do not pop up a window, just keep a log
    }

    private static void ShowCrashReport(string summary, string source, Exception ex)
    {
        var diagnostic = CrashReportWindow.BuildDiagnostic(source, ex, CrashReportWindow.CrashLogPath);
        try
        {
            var window = new CrashReportWindow(summary, diagnostic);
            if (Application.Current?.MainWindow is { IsLoaded: true } owner)
            {
                window.Owner = owner;
            }

            window.ShowDialog();
        }
        catch (Exception)
        {
            // Fall back to the system message box if the report window itself cannot be created, so the user at least knows what happened
            MessageBox.Show($"{summary}{Environment.NewLine}{Environment.NewLine}" +
                            $"Details: {CrashReportWindow.CrashLogPath}",
                "UnpackStation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void WriteCrashLog(string source, Exception ex)
    {
        try
        {
            var path = CrashReportWindow.CrashLogPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch
        {
            // Do not throw if writing the crash log itself fails, otherwise it would cause a second crash
        }
    }
}
