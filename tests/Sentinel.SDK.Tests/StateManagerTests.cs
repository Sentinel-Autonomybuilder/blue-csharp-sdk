using System.Diagnostics;
using System.Text.Json;
using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for StateManager — VPN state persistence, session tracking,
/// PID file management, and orphan recovery.
/// Uses real file I/O against the platform state directory.
/// </summary>
public class StateManagerTests : IDisposable
{
    /// <summary>
    /// Clean up any state left behind by tests.
    /// </summary>
    public void Dispose()
    {
        StateManager.ClearState();
        StateManager.ClearPidFile("test-statemanager");
        GC.SuppressFinalize(this);
    }

    // ─── VpnState Record ───

    [Fact]
    public void VpnState_Creation_AllFields()
    {
        var state = new VpnState(
            SessionId: "12345",
            ServiceType: "wireguard",
            WgTunnelName: "wgsent0",
            V2RayPid: null,
            SocksPort: null,
            SystemProxySet: false,
            NodeAddress: "sentnode1abcdefghijklmnopqrstuvwxyz01234abc",
            ConfPath: @"C:\ProgramData\sentinel-wg\wgsent0.conf",
            SavedAt: "2026-03-18T12:00:00Z",
            Pid: 1234
        );

        Assert.Equal("12345", state.SessionId);
        Assert.Equal("wireguard", state.ServiceType);
        Assert.Equal("wgsent0", state.WgTunnelName);
        Assert.Null(state.V2RayPid);
        Assert.Null(state.SocksPort);
        Assert.False(state.SystemProxySet);
        Assert.Equal("sentnode1abcdefghijklmnopqrstuvwxyz01234abc", state.NodeAddress);
        Assert.Equal(@"C:\ProgramData\sentinel-wg\wgsent0.conf", state.ConfPath);
        Assert.Equal("2026-03-18T12:00:00Z", state.SavedAt);
        Assert.Equal(1234, state.Pid);
    }

    [Fact]
    public void VpnState_V2RayFields()
    {
        var state = new VpnState(
            SessionId: "99999",
            ServiceType: "v2ray",
            WgTunnelName: null,
            V2RayPid: 5678,
            SocksPort: 10808,
            SystemProxySet: true,
            NodeAddress: "sentnode1abcdefghijklmnopqrstuvwxyz01234abc",
            ConfPath: null,
            SavedAt: "2026-03-18T13:00:00Z",
            Pid: 9999
        );

        Assert.Equal("v2ray", state.ServiceType);
        Assert.Equal(5678, state.V2RayPid);
        Assert.Equal(10808, state.SocksPort);
        Assert.True(state.SystemProxySet);
    }

    // ─── SaveState / LoadState Round-Trip ───

    [Fact]
    public void SaveState_ThenLoadState_RoundTrips()
    {
        var original = new VpnState(
            SessionId: "42",
            ServiceType: "wireguard",
            WgTunnelName: "wgsent0",
            V2RayPid: null,
            SocksPort: null,
            SystemProxySet: false,
            NodeAddress: "sentnode1abcdefghijklmnopqrstuvwxyz01234abc",
            ConfPath: null,
            SavedAt: "2026-03-18T10:00:00Z",
            Pid: Environment.ProcessId
        );

        StateManager.SaveState(original);
        var loaded = StateManager.LoadState();

        Assert.NotNull(loaded);
        Assert.Equal(original.SessionId, loaded!.SessionId);
        Assert.Equal(original.ServiceType, loaded.ServiceType);
        Assert.Equal(original.WgTunnelName, loaded.WgTunnelName);
        Assert.Equal(original.V2RayPid, loaded.V2RayPid);
        Assert.Equal(original.SocksPort, loaded.SocksPort);
        Assert.Equal(original.SystemProxySet, loaded.SystemProxySet);
        Assert.Equal(original.NodeAddress, loaded.NodeAddress);
        Assert.Equal(original.Pid, loaded.Pid);
    }

    // ─── ClearState ───

    [Fact]
    public void ClearState_RemovesState()
    {
        var state = new VpnState(
            SessionId: "100",
            ServiceType: "wireguard",
            WgTunnelName: "wgsent0",
            V2RayPid: null,
            SocksPort: null,
            SystemProxySet: false,
            NodeAddress: null,
            ConfPath: null,
            SavedAt: "2026-03-18T10:00:00Z",
            Pid: Environment.ProcessId
        );

        StateManager.SaveState(state);
        Assert.NotNull(StateManager.LoadState());

        StateManager.ClearState();
        Assert.Null(StateManager.LoadState());
    }

    // ─── LoadState Returns Null When No State ───

    [Fact]
    public void LoadState_ReturnsNull_WhenNoState()
    {
        StateManager.ClearState();

        var loaded = StateManager.LoadState();

        Assert.Null(loaded);
    }

    // ─── Session Poisoning ───

    [Fact]
    public void MarkSessionPoisoned_MakesIsSessionPoisoned_ReturnTrue()
    {
        var sessionId = $"poison-{Guid.NewGuid():N}";

        StateManager.MarkSessionPoisoned(
            sessionId,
            "sentnode1abcdefghijklmnopqrstuvwxyz01234abc",
            "handshake timeout");

        Assert.True(StateManager.IsSessionPoisoned(sessionId));
    }

    [Fact]
    public void MarkSessionActive_MakesIsSessionPoisoned_ReturnFalse()
    {
        var sessionId = $"active-{Guid.NewGuid():N}";

        // First poison it
        StateManager.MarkSessionPoisoned(
            sessionId,
            "sentnode1abcdefghijklmnopqrstuvwxyz01234abc",
            "some error");
        Assert.True(StateManager.IsSessionPoisoned(sessionId));

        // Then mark active — should overwrite poisoned status
        StateManager.MarkSessionActive(
            sessionId,
            "sentnode1abcdefghijklmnopqrstuvwxyz01234abc");

        Assert.False(StateManager.IsSessionPoisoned(sessionId));
    }

    [Fact]
    public void IsSessionPoisoned_ReturnsFalse_ForUnknownSession()
    {
        Assert.False(StateManager.IsSessionPoisoned("nonexistent-session-id-999"));
    }

    // ─── GetSessionHistory ───

    [Fact]
    public void GetSessionHistory_ReturnsDictionary()
    {
        var sessionId = $"history-{Guid.NewGuid():N}";

        StateManager.MarkSessionActive(
            sessionId,
            "sentnode1abcdefghijklmnopqrstuvwxyz01234abc");

        var history = StateManager.GetSessionHistory();

        Assert.NotNull(history);
        Assert.IsType<Dictionary<string, SessionRecord>>(history);
        Assert.True(history.ContainsKey(sessionId));
        Assert.Equal("active", history[sessionId].Status);
    }

    // ─── PID File ───

    [Fact]
    public void WritePidFile_ReturnsPathString()
    {
        var path = StateManager.WritePidFile("test-statemanager");

        Assert.False(string.IsNullOrEmpty(path));
        Assert.Contains("test-statemanager.pid", path);

        // Clean up
        StateManager.ClearPidFile("test-statemanager");
    }

    [Fact]
    public void CheckPidFile_ReturnsRunningTrue_ForCurrentProcess()
    {
        StateManager.WritePidFile("test-statemanager");

        var check = StateManager.CheckPidFile("test-statemanager");

        Assert.True(check.Running);
        Assert.Equal(Environment.ProcessId, check.Pid);
        Assert.NotNull(check.StartedAt);

        // Clean up
        StateManager.ClearPidFile("test-statemanager");
    }

    [Fact]
    public void CheckPidFile_ReturnsRunningFalse_WhenNoPidFile()
    {
        StateManager.ClearPidFile("test-nonexistent");

        var check = StateManager.CheckPidFile("test-nonexistent");

        Assert.False(check.Running);
        Assert.Null(check.Pid);
    }

    [Fact]
    public void ClearPidFile_RemovesFile()
    {
        StateManager.WritePidFile("test-statemanager");
        StateManager.ClearPidFile("test-statemanager");

        var check = StateManager.CheckPidFile("test-statemanager");

        Assert.False(check.Running);
    }

    // ─── RecoverOrphans ───

    [Fact]
    public void RecoverOrphans_ReturnsRecoverResult()
    {
        // Ensure no state exists
        StateManager.ClearState();

        var result = StateManager.RecoverOrphans();

        Assert.NotNull(result);
        Assert.IsType<RecoverResult>(result);
        Assert.False(result.HadState);
        Assert.Empty(result.Cleaned);
    }

    [Fact]
    public void RecoverOrphans_WithState_ReturnsHadStateTrue()
    {
        // Ensure clean slate
        StateManager.ClearState();

        // Save state with the current process PID
        var state = new VpnState(
            SessionId: "77",
            ServiceType: "wireguard",
            WgTunnelName: "wgsent0",
            V2RayPid: null,
            SocksPort: null,
            SystemProxySet: false,
            NodeAddress: "sentnode1abcdefghijklmnopqrstuvwxyz01234abc",
            ConfPath: null,
            SavedAt: DateTime.UtcNow.ToString("o"),
            Pid: Environment.ProcessId
        );

        StateManager.SaveState(state);

        var result = StateManager.RecoverOrphans();

        // State file existed, so HadState should be true
        Assert.True(result.HadState);
        // After recovery, state should be cleared (either cleaned or process still alive)
        Assert.NotNull(result.Cleaned);
    }

    // ─── Record Types ───

    [Fact]
    public void RecoverResult_Creation()
    {
        var result = new RecoverResult(true, ["v2ray PID 1234", "WireGuard tunnel wgsent0"]);

        Assert.True(result.HadState);
        Assert.Equal(2, result.Cleaned.Length);
        Assert.Contains("v2ray PID 1234", result.Cleaned);
    }

    [Fact]
    public void SessionRecord_Creation()
    {
        var record = new SessionRecord(
            SessionId: "555",
            NodeAddress: "sentnode1abcdefghijklmnopqrstuvwxyz01234abc",
            Status: "poisoned",
            Timestamp: "2026-03-18T10:00:00Z",
            Error: "handshake timeout"
        );

        Assert.Equal("555", record.SessionId);
        Assert.Equal("poisoned", record.Status);
        Assert.Equal("handshake timeout", record.Error);
    }

    [Fact]
    public void PidCheck_Creation()
    {
        var check = new PidCheck(true, 1234, "2026-03-18T10:00:00Z");

        Assert.True(check.Running);
        Assert.Equal(1234, check.Pid);
        Assert.Equal("2026-03-18T10:00:00Z", check.StartedAt);
    }

    [Fact]
    public void PidCheck_NotRunning()
    {
        var check = new PidCheck(false, null, null);

        Assert.False(check.Running);
        Assert.Null(check.Pid);
        Assert.Null(check.StartedAt);
    }
}
