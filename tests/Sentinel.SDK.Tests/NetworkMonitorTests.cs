using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for NetworkMonitor — system network change detection.
/// </summary>
public class NetworkMonitorTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); }
            catch { /* best effort cleanup */ }
        }
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var monitor = new NetworkMonitor();
        _disposables.Add(monitor);

        // If we get here, constructor succeeded without throwing
        Assert.NotNull(monitor);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var monitor = new NetworkMonitor();
        monitor.Dispose();

        // Double dispose should also be safe
        monitor.Dispose();
    }

    [Fact]
    public void Dispose_UnsubscribesEvents_NoCrashOnGC()
    {
        var monitor = new NetworkMonitor();
        var raised = false;
        monitor.NetworkChanged += (_, _) => raised = true;

        monitor.Dispose();

        // After dispose, no crash and event handler is cleaned up
        Assert.False(raised);
    }

    [Fact]
    public void NetworkChangedEventArgs_HasReasonProperty()
    {
        var args = new NetworkChangedEventArgs { Reason = "unavailable" };
        Assert.Equal("unavailable", args.Reason);
    }

    [Fact]
    public void NetworkChangedEventArgs_DefaultReason_IsEmptyString()
    {
        var args = new NetworkChangedEventArgs();
        Assert.Equal("", args.Reason);
    }

    [Fact]
    public void NetworkChangedEventArgs_AllReasonValues()
    {
        var available = new NetworkChangedEventArgs { Reason = "available" };
        var unavailable = new NetworkChangedEventArgs { Reason = "unavailable" };
        var addressChanged = new NetworkChangedEventArgs { Reason = "address_changed" };

        Assert.Equal("available", available.Reason);
        Assert.Equal("unavailable", unavailable.Reason);
        Assert.Equal("address_changed", addressChanged.Reason);
    }

    [Fact]
    public void CanSubscribeAndUnsubscribe_WithoutErrors()
    {
        var monitor = new NetworkMonitor();
        _disposables.Add(monitor);

        void Handler(object? sender, NetworkChangedEventArgs e) { }

        monitor.NetworkChanged += Handler;
        monitor.NetworkChanged -= Handler;
    }
}
