using System.IO;
using System.Windows;
using System.Windows.Threading;
using HandshakeDVPN.Services;

namespace HandshakeDVPN;

public partial class App : Application
{
    public static IHnsVpnBackend Backend { get; private set; } = null!;

    private static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandshakeDVPN", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Catch all unhandled exceptions
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Backend = new NativeVpnClient(null);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("Dispatcher", e.Exception);
        e.Handled = true; // prevent crash, keep app running
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) LogCrash("Domain", ex);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("Task", e.Exception);
        e.SetObserved(); // prevent crash
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(CrashLog)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var msg = $"[{DateTime.Now:HH:mm:ss}] [{source}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n";
            File.AppendAllText(CrashLog, msg);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Backend?.Dispose();
        base.OnExit(e);
    }
}
