using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for AutoReconnect — VPN connection health monitor with
/// exponential backoff, configurable retries, and event notifications.
/// </summary>
public class AutoReconnectTests : IDisposable
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

    private AutoReconnect Track(AutoReconnect ar)
    {
        _disposables.Add(ar);
        return ar;
    }

    // ─── Constructor Doesn't Throw ───

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var ar = Track(new AutoReconnect(
            isConnected: () => true,
            reconnect: () => Task.CompletedTask));

        Assert.NotNull(ar);
    }

    [Fact]
    public void Constructor_ThrowsOnNullIsConnected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AutoReconnect(null!, () => Task.CompletedTask));
    }

    [Fact]
    public void Constructor_ThrowsOnNullReconnect()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AutoReconnect(() => true, null!));
    }

    // ─── Start Begins Monitoring ───

    [Fact]
    public void Start_BeginsMonitoring_WithoutThrowing()
    {
        var pollCount = 0;

        var ar = Track(new AutoReconnect(
            isConnected: () => { Interlocked.Increment(ref pollCount); return true; },
            reconnect: () => Task.CompletedTask,
            options: new AutoReconnectOptions(PollIntervalMs: 50)));

        ar.Start();

        // Wait for at least one poll tick
        Thread.Sleep(200);

        Assert.True(pollCount > 0);
    }

    // ─── Start Is Idempotent ───

    [Fact]
    public void Start_IsIdempotent_CalledTwice()
    {
        var ar = Track(new AutoReconnect(
            isConnected: () => true,
            reconnect: () => Task.CompletedTask,
            options: new AutoReconnectOptions(PollIntervalMs: 50)));

        ar.Start();
        ar.Start(); // Should be no-op

        // No exception = success
    }

    // ─── Stop Stops Monitoring ───

    [Fact]
    public void Stop_StopsMonitoring()
    {
        var pollCount = 0;

        var ar = Track(new AutoReconnect(
            isConnected: () => { Interlocked.Increment(ref pollCount); return true; },
            reconnect: () => Task.CompletedTask,
            options: new AutoReconnectOptions(PollIntervalMs: 50)));

        ar.Start();
        Thread.Sleep(150);
        ar.Stop();

        var countAfterStop = pollCount;
        Thread.Sleep(200);

        // Poll count should not have increased significantly after stop
        // (allow +1 for in-flight tick)
        Assert.True(pollCount <= countAfterStop + 1);
    }

    [Fact]
    public void Stop_CanBeCalledMultipleTimes()
    {
        var ar = Track(new AutoReconnect(
            isConnected: () => true,
            reconnect: () => Task.CompletedTask));

        ar.Stop();
        ar.Stop();

        // No exception = success
    }

    // ─── Reconnecting Event Fires ───

    [Fact]
    public void Reconnecting_EventFires_WhenConnectionDrops()
    {
        var connected = true;
        var reconnectingAttempts = new List<int>();
        var reconnectingEvent = new ManualResetEventSlim(false);

        var ar = Track(new AutoReconnect(
            isConnected: () => connected,
            reconnect: () =>
            {
                connected = true;
                return Task.CompletedTask;
            },
            options: new AutoReconnectOptions(
                PollIntervalMs: 50,
                MaxRetries: 3,
                BackoffMs: new[] { 10 })));

        ar.Reconnecting += (_, attempt) =>
        {
            reconnectingAttempts.Add(attempt);
            reconnectingEvent.Set();
        };

        ar.Start();

        // Let it see we're connected first
        Thread.Sleep(150);

        // Simulate connection drop
        connected = false;

        // Wait for the Reconnecting event to fire
        var fired = reconnectingEvent.Wait(TimeSpan.FromSeconds(3));
        ar.Stop();

        Assert.True(fired, "Reconnecting event did not fire within timeout");
        Assert.NotEmpty(reconnectingAttempts);
        Assert.Equal(1, reconnectingAttempts[0]); // 1-based attempt
    }

    // ─── GaveUp Event Fires ───

    [Fact]
    public void GaveUp_EventFires_AfterMaxRetries()
    {
        var connected = true;
        Exception? gaveUpException = null;
        var gaveUpEvent = new ManualResetEventSlim(false);

        var ar = Track(new AutoReconnect(
            isConnected: () => connected,
            reconnect: () => throw new InvalidOperationException("reconnect failed"),
            options: new AutoReconnectOptions(
                PollIntervalMs: 50,
                MaxRetries: 2,
                BackoffMs: new[] { 10 })));

        ar.GaveUp += (_, ex) =>
        {
            gaveUpException = ex;
            gaveUpEvent.Set();
        };

        ar.Start();

        // Let it see we're connected
        Thread.Sleep(150);

        // Drop connection — will trigger reconnect attempts that all fail
        connected = false;

        var fired = gaveUpEvent.Wait(TimeSpan.FromSeconds(5));
        ar.Stop();

        Assert.True(fired, "GaveUp event did not fire within timeout");
        Assert.NotNull(gaveUpException);
    }

    // ─── Dispose Stops Monitoring ───

    [Fact]
    public void Dispose_StopsMonitoring()
    {
        var pollCount = 0;

        var ar = new AutoReconnect(
            isConnected: () => { Interlocked.Increment(ref pollCount); return true; },
            reconnect: () => Task.CompletedTask,
            options: new AutoReconnectOptions(PollIntervalMs: 50));

        ar.Start();
        Thread.Sleep(150);
        ar.Dispose();

        var countAfterDispose = pollCount;
        Thread.Sleep(200);

        Assert.True(pollCount <= countAfterDispose + 1);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var ar = new AutoReconnect(
            isConnected: () => true,
            reconnect: () => Task.CompletedTask);

        ar.Dispose();
        ar.Dispose(); // Should not throw
    }

    [Fact]
    public void Start_ThrowsObjectDisposedException_AfterDispose()
    {
        var ar = new AutoReconnect(
            isConnected: () => true,
            reconnect: () => Task.CompletedTask);

        ar.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ar.Start());
    }

    // ─── Doesn't Reconnect If Never Connected ───

    [Fact]
    public void DoesNotReconnect_IfNeverWasConnected()
    {
        var reconnectCalled = false;
        var reconnectEvent = new ManualResetEventSlim(false);

        var ar = Track(new AutoReconnect(
            isConnected: () => false, // Always disconnected from the start
            reconnect: () =>
            {
                reconnectCalled = true;
                reconnectEvent.Set();
                return Task.CompletedTask;
            },
            options: new AutoReconnectOptions(
                PollIntervalMs: 50,
                MaxRetries: 3,
                BackoffMs: new[] { 10 })));

        ar.Start();

        // Wait enough time for several poll cycles
        var fired = reconnectEvent.Wait(TimeSpan.FromMilliseconds(500));
        ar.Stop();

        Assert.False(fired, "Reconnect should not be attempted when never connected");
        Assert.False(reconnectCalled);
    }

    // ─── Options Defaults ───

    [Fact]
    public void AutoReconnectOptions_HasSensibleDefaults()
    {
        var opts = new AutoReconnectOptions();

        Assert.True(opts.Enabled);
        Assert.Equal(5000, opts.PollIntervalMs);
        Assert.Equal(5, opts.MaxRetries);
        Assert.Null(opts.BackoffMs); // Uses DefaultBackoffMs internally
    }

    // ─── BackoffMs Custom Schedule ───

    [Fact]
    public void AutoReconnectOptions_CustomBackoff_IsStored()
    {
        var custom = new[] { 100, 200, 300 };
        var opts = new AutoReconnectOptions(BackoffMs: custom);

        Assert.Equal(custom, opts.BackoffMs);
    }

    [Fact]
    public void AutoReconnectOptions_DefaultBackoff_IsNull()
    {
        var opts = new AutoReconnectOptions();

        Assert.Null(opts.BackoffMs); // Internal DefaultBackoffMs used at runtime
    }

    // ─── Disabled Option ───

    [Fact]
    public void Start_DoesNothing_WhenDisabled()
    {
        var pollCount = 0;

        var ar = Track(new AutoReconnect(
            isConnected: () => { Interlocked.Increment(ref pollCount); return true; },
            reconnect: () => Task.CompletedTask,
            options: new AutoReconnectOptions(Enabled: false, PollIntervalMs: 50)));

        ar.Start();
        Thread.Sleep(200);
        ar.Stop();

        Assert.Equal(0, pollCount);
    }

    // ─── Reconnected Event ───

    [Fact]
    public void Reconnected_EventFires_OnSuccessfulReconnect()
    {
        var connected = true;
        var reconnectedFired = new ManualResetEventSlim(false);

        var ar = Track(new AutoReconnect(
            isConnected: () => connected,
            reconnect: () =>
            {
                connected = true;
                return Task.CompletedTask;
            },
            options: new AutoReconnectOptions(
                PollIntervalMs: 50,
                MaxRetries: 3,
                BackoffMs: new[] { 10 })));

        ar.Reconnected += (_, _) => reconnectedFired.Set();

        ar.Start();
        Thread.Sleep(150); // Let it see connected

        connected = false; // Drop
        var fired = reconnectedFired.Wait(TimeSpan.FromSeconds(3));
        ar.Stop();

        Assert.True(fired, "Reconnected event should fire after successful reconnect");
    }
}
