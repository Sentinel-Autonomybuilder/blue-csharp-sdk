using System.Runtime.CompilerServices;

namespace Sentinel.SDK.Core;

// ─── Auto-Reconnect Options ─────────────────────────────────────────────────

/// <summary>
/// Configuration for <see cref="AutoReconnect"/> behavior.
/// </summary>
/// <param name="Enabled">Whether auto-reconnect is active. Default: <c>true</c>.</param>
/// <param name="PollIntervalMs">How often to check connection health, in milliseconds. Default: 5000.</param>
/// <param name="MaxRetries">Maximum reconnection attempts before giving up. Default: 5.</param>
/// <param name="BackoffMs">
/// Exponential backoff delays in milliseconds, indexed by retry attempt (0-based).
/// If the retry count exceeds the array length, the last value is reused.
/// Default: <c>[1000, 2000, 5000, 10000, 30000]</c>.
/// </param>
public record AutoReconnectOptions(
    bool Enabled = true,
    int PollIntervalMs = 5000,
    int MaxRetries = 5,
    int[]? BackoffMs = null
)
{
    /// <summary>Default backoff schedule: 1s, 2s, 5s, 10s, 30s.</summary>
    internal static readonly int[] DefaultBackoffMs = [1000, 2000, 5000, 10000, 30000];

    /// <summary>
    /// Get the backoff delay for the given retry attempt (0-based).
    /// Clamps to the last element if the attempt exceeds the array length.
    /// </summary>
    internal int GetBackoffMs(int attempt)
    {
        var schedule = BackoffMs ?? DefaultBackoffMs;
        if (schedule.Length == 0) return 1000;
        var index = Math.Min(attempt, schedule.Length - 1);
        return Math.Max(0, schedule[index]);
    }
}

// ─── Auto-Reconnect ─────────────────────────────────────────────────────────

/// <summary>
/// Monitors VPN connection health and auto-reconnects on failure.
/// Uses exponential backoff with configurable delays.
/// <para>
/// The monitor polls on a <see cref="System.Threading.Timer"/> at the configured
/// interval. When a previously healthy connection drops, it enters the reconnect
/// loop: fire <see cref="Reconnecting"/>, wait for the backoff delay, then call
/// the reconnect delegate. If reconnection succeeds, fire <see cref="Reconnected"/>
/// and reset the retry counter. If all retries are exhausted, fire <see cref="GaveUp"/>
/// and stop monitoring.
/// </para>
/// <para>Thread-safe. All state mutations are guarded by a lock.</para>
/// </summary>
public class AutoReconnect : IDisposable, IAsyncDisposable
{
    private readonly Func<bool> _isConnected;
    private readonly Func<Task> _reconnect;
    private readonly AutoReconnectOptions _options;
    private readonly object _lock = new();

    private Timer? _timer;
    private bool _wasConnected;
    private int _retries;
    private bool _disposed;
    private bool _reconnecting;

    // ─── Events ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when a reconnection attempt is about to start.
    /// The <see cref="int"/> argument is the 1-based attempt number.
    /// </summary>
    public event EventHandler<int>? Reconnecting;

    /// <summary>
    /// Raised when a reconnection attempt succeeds and the connection is restored.
    /// </summary>
    public event EventHandler? Reconnected;

    /// <summary>
    /// Raised when all reconnection attempts are exhausted. The <see cref="Exception"/>
    /// argument contains the error from the last reconnect attempt, or a generic message
    /// if the connection simply could not be restored.
    /// </summary>
    public event EventHandler<Exception>? GaveUp;

    // ─── Constructor ────────────────────────────────────────────────────────

    /// <summary>
    /// Create a new auto-reconnect monitor.
    /// </summary>
    /// <param name="isConnected">
    /// Delegate that returns <c>true</c> when the VPN connection is healthy.
    /// Called on the timer thread — must be thread-safe and non-blocking.
    /// </param>
    /// <param name="reconnect">
    /// Async delegate that attempts to re-establish the VPN connection.
    /// Should throw on failure so the monitor can retry.
    /// </param>
    /// <param name="options">
    /// Configuration options. Pass <c>null</c> for defaults (5s poll, 5 retries,
    /// exponential backoff 1s/2s/5s/10s/30s).
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="isConnected"/> or <paramref name="reconnect"/> is null.
    /// </exception>
    public AutoReconnect(Func<bool> isConnected, Func<Task> reconnect, AutoReconnectOptions? options = null)
    {
        _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
        _reconnect = reconnect ?? throw new ArgumentNullException(nameof(reconnect));
        _options = options ?? new AutoReconnectOptions();
    }

    // ─── Start ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Start monitoring the connection. If the monitor is already running,
    /// this call is a no-op.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the instance has been disposed.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_timer is not null) return;
            if (!_options.Enabled) return;

            _retries = 0;
            _wasConnected = false;
            _reconnecting = false;

            _timer = new Timer(
                OnTick,
                null,
                _options.PollIntervalMs,
                _options.PollIntervalMs
            );
        }
    }

    // ─── Stop ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Stop monitoring the connection. The timer is disposed and state is reset.
    /// Safe to call multiple times.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            StopInternal();
        }
    }

    // ─── IAsyncDisposable ────────────────────────────────────────────────────

    /// <summary>
    /// Asynchronously dispose the monitor, stopping monitoring and releasing resources.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Stop();
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ─── Dispose ────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispose the monitor, releasing the timer and preventing further use.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            _disposed = true;
            StopInternal();
        }

        GC.SuppressFinalize(this);
    }

    // ─── Timer Callback ─────────────────────────────────────────────────────

    /// <summary>
    /// Timer tick handler. Checks connection status and drives the reconnect loop.
    /// Re-entrancy is guarded: if a reconnect is already in progress, the tick is skipped.
    /// </summary>
    private async void OnTick(object? state)
    {
        // Guard against re-entrancy (reconnect may take longer than poll interval)
        lock (_lock)
        {
            if (_disposed || _reconnecting) return;
        }

        bool connected;
        try
        {
            connected = _isConnected();
        }
        catch
        {
            // Treat exceptions from the health check as "not connected"
            connected = false;
        }

        if (connected)
        {
            lock (_lock)
            {
                _wasConnected = true;
                _retries = 0;
            }
            return;
        }

        // Not connected — only attempt reconnect if we were previously connected
        bool shouldReconnect;
        int attempt;
        lock (_lock)
        {
            if (!_wasConnected) return;
            if (_reconnecting) return;

            _retries++;
            attempt = _retries;

            if (_retries > _options.MaxRetries)
            {
                StopInternal();
                RaiseGaveUp(new SentinelException(
                    ErrorCodes.AllNodesFailed,
                    $"Auto-reconnect gave up after {_options.MaxRetries} attempts"
                ));
                return;
            }

            _reconnecting = true;
            shouldReconnect = true;
        }

        if (!shouldReconnect) return;

        try
        {
            RaiseReconnecting(attempt);

            // Wait for the backoff delay before attempting reconnect
            var delayMs = _options.GetBackoffMs(attempt - 1);
            if (delayMs > 0)
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
            }

            // Check if we were stopped/disposed during the delay
            lock (_lock)
            {
                if (_disposed || _timer is null)
                {
                    _reconnecting = false;
                    return;
                }
            }

            await _reconnect().ConfigureAwait(false);

            // Reconnect succeeded
            lock (_lock)
            {
                _retries = 0;
                _wasConnected = true;
                _reconnecting = false;
            }

            RaiseReconnected();
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _reconnecting = false;

                // If we've now exhausted all retries, give up
                if (_retries >= _options.MaxRetries)
                {
                    StopInternal();
                    RaiseGaveUp(ex);
                }
            }
        }
    }

    // ─── Private Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Stop the timer without acquiring the lock (caller must hold it).
    /// </summary>
    private void StopInternal()
    {
        _timer?.Dispose();
        _timer = null;
        _retries = 0;
        _reconnecting = false;
    }

    /// <summary>Raise <see cref="Reconnecting"/> safely on a ThreadPool thread
    /// to prevent deadlocks when handlers use sync-over-async (e.g. WPF/WinUI UI thread).</summary>
    private void RaiseReconnecting(int attempt)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Reconnecting?.Invoke(this, attempt); }
            catch { /* Event handler exceptions must not crash the monitor */ }
        });
    }

    /// <summary>Raise <see cref="Reconnected"/> safely on a ThreadPool thread.</summary>
    private void RaiseReconnected()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Reconnected?.Invoke(this, EventArgs.Empty); }
            catch { /* Event handler exceptions must not crash the monitor */ }
        });
    }

    /// <summary>Raise <see cref="GaveUp"/> safely on a ThreadPool thread.</summary>
    private void RaiseGaveUp(Exception ex)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { GaveUp?.Invoke(this, ex); }
            catch { /* Event handler exceptions must not crash the monitor */ }
        });
    }
}
