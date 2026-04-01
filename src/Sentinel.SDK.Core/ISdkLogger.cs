namespace Sentinel.SDK.Core;

/// <summary>
/// Minimal logging interface for SDK diagnostics.
/// Implement this to capture SDK logs in your application's logging framework.
/// </summary>
public interface ISdkLogger
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>Default logger that writes to Console.Error. Used when no custom logger is provided.</summary>
public class ConsoleSdkLogger : ISdkLogger
{
    public void Debug(string message) { }  // silent by default
    public void Info(string message) => Console.Error.WriteLine($"[Sentinel] {message}");
    public void Warn(string message) => Console.Error.WriteLine($"[Sentinel WARN] {message}");
    public void Error(string message, Exception? ex = null) => Console.Error.WriteLine($"[Sentinel ERROR] {message}{(ex != null ? $" — {ex.Message}" : "")}");
}

/// <summary>Null logger — discards all messages.</summary>
public class NullSdkLogger : ISdkLogger
{
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}
