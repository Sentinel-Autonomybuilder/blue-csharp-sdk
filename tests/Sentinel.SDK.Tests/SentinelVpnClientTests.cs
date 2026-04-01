using Sentinel.SDK.Core;
using Sentinel.SDK.Node;
using Xunit;

namespace Sentinel.SDK.Tests;

public class SentinelVpnOptionsTests
{
    [Fact]
    public void Defaults_FullTunnel_IsTrue()
    {
        var opts = new SentinelVpnOptions();
        Assert.True(opts.FullTunnel);
    }

    [Fact]
    public void Defaults_SystemProxy_IsTrue()
    {
        var opts = new SentinelVpnOptions();
        Assert.True(opts.SystemProxy);
    }

    [Fact]
    public void Defaults_Gigabytes_IsOne()
    {
        var opts = new SentinelVpnOptions();
        Assert.Equal(1, opts.Gigabytes);
    }

    [Fact]
    public void Defaults_ForceNewSession_IsFalse()
    {
        var opts = new SentinelVpnOptions();
        Assert.False(opts.ForceNewSession);
    }

    [Fact]
    public void Defaults_LcdUrls_IsNull()
    {
        var opts = new SentinelVpnOptions();
        Assert.Null(opts.LcdUrls);
    }

    [Fact]
    public void Defaults_RpcUrls_IsNull()
    {
        var opts = new SentinelVpnOptions();
        Assert.Null(opts.RpcUrls);
    }

    [Fact]
    public void Defaults_V2RayExePath_IsNull()
    {
        var opts = new SentinelVpnOptions();
        Assert.Null(opts.V2RayExePath);
    }

    [Fact]
    public void WithInit_OverridesDefaults()
    {
        var opts = new SentinelVpnOptions
        {
            FullTunnel = false,
            SystemProxy = false,
            Gigabytes = 5,
            ForceNewSession = true,
            V2RayExePath = @"C:\v2ray\v2ray.exe",
        };

        Assert.False(opts.FullTunnel);
        Assert.False(opts.SystemProxy);
        Assert.Equal(5, opts.Gigabytes);
        Assert.True(opts.ForceNewSession);
        Assert.Equal(@"C:\v2ray\v2ray.exe", opts.V2RayExePath);
    }
}

public class ConnectAutoOptionsTests
{
    [Fact]
    public void Defaults_MaxAttempts_IsThree()
    {
        var opts = new ConnectAutoOptions();
        Assert.Equal(3, opts.MaxAttempts);
    }

    [Fact]
    public void Defaults_Countries_IsNull()
    {
        var opts = new ConnectAutoOptions();
        Assert.Null(opts.Countries);
    }

    [Fact]
    public void Defaults_ServiceType_IsNull()
    {
        var opts = new ConnectAutoOptions();
        Assert.Null(opts.ServiceType);
    }

    [Fact]
    public void Defaults_NodePool_IsNull()
    {
        var opts = new ConnectAutoOptions();
        Assert.Null(opts.NodePool);
    }

    [Fact]
    public void WithInit_SetsAllFields()
    {
        var opts = new ConnectAutoOptions
        {
            MaxAttempts = 10,
            Countries = new[] { "DE", "US" },
            ServiceType = "wireguard",
            NodePool = new[] { "sentnode1abc", "sentnode1def" },
        };

        Assert.Equal(10, opts.MaxAttempts);
        Assert.Equal(new[] { "DE", "US" }, opts.Countries);
        Assert.Equal("wireguard", opts.ServiceType);
        Assert.Equal(2, opts.NodePool.Length);
    }
}

public class ConnectionResultTests
{
    [Fact]
    public void Defaults_AllFieldsHaveDefaults()
    {
        var result = new ConnectionResult();

        Assert.Equal("", result.SessionId);
        Assert.Equal("", result.NodeAddress);
        Assert.Equal("", result.ServiceType);
        Assert.Null(result.SocksPort);
        Assert.Null(result.SocksUser);
        Assert.Null(result.SocksPass);
        Assert.Null(result.VpnIp);
        Assert.Null(result.Verification);
    }

    [Fact]
    public void WithInit_SetsV2RayFields()
    {
        var result = new ConnectionResult
        {
            SessionId = "42",
            NodeAddress = "sentnode1abc",
            ServiceType = "v2ray",
            SocksPort = 10808,
            SocksUser = "user",
            SocksPass = "pass",
        };

        Assert.Equal("42", result.SessionId);
        Assert.Equal("sentnode1abc", result.NodeAddress);
        Assert.Equal("v2ray", result.ServiceType);
        Assert.Equal(10808, result.SocksPort);
        Assert.Equal("user", result.SocksUser);
        Assert.Equal("pass", result.SocksPass);
    }

    [Fact]
    public void WithInit_SetsWireGuardFields()
    {
        var result = new ConnectionResult
        {
            SessionId = "99",
            NodeAddress = "sentnode1xyz",
            ServiceType = "wireguard",
            VpnIp = "10.8.0.2",
        };

        Assert.Equal("10.8.0.2", result.VpnIp);
        Assert.Null(result.SocksPort);
    }
}

public class ConnectionStatusTests
{
    [Fact]
    public void Defaults_Connected_IsFalse()
    {
        var status = new ConnectionStatus();
        Assert.False(status.Connected);
    }

    [Fact]
    public void Defaults_NullableFields_AreNull()
    {
        var status = new ConnectionStatus();
        Assert.Null(status.NodeAddress);
        Assert.Null(status.SessionId);
        Assert.Null(status.ServiceType);
    }

    [Fact]
    public void Defaults_Uptime_IsZero()
    {
        var status = new ConnectionStatus();
        Assert.Equal(TimeSpan.Zero, status.Uptime);
    }

    [Fact]
    public void WithInit_SetsAllFields()
    {
        var uptime = TimeSpan.FromMinutes(5);
        var status = new ConnectionStatus
        {
            Connected = true,
            NodeAddress = "sentnode1abc",
            SessionId = "123",
            ServiceType = "wireguard",
            Uptime = uptime,
        };

        Assert.True(status.Connected);
        Assert.Equal("sentnode1abc", status.NodeAddress);
        Assert.Equal("123", status.SessionId);
        Assert.Equal("wireguard", status.ServiceType);
        Assert.Equal(uptime, status.Uptime);
    }
}

public class ConnectionVerificationTests
{
    [Fact]
    public void Creates_WithWorkingAndIp()
    {
        var v = new ConnectionVerification(true, "1.2.3.4");

        Assert.True(v.Working);
        Assert.Equal("1.2.3.4", v.VpnIp);
    }

    [Fact]
    public void Creates_WithFailedAndNullIp()
    {
        var v = new ConnectionVerification(false, null);

        Assert.False(v.Working);
        Assert.Null(v.VpnIp);
    }

    [Fact]
    public void SupportsValueEquality()
    {
        var v1 = new ConnectionVerification(true, "1.2.3.4");
        var v2 = new ConnectionVerification(true, "1.2.3.4");

        Assert.Equal(v1, v2);
    }
}

public class VpnEventArgsTests
{
    [Fact]
    public void ProgressEventArgs_DefaultsEmpty()
    {
        var args = new ProgressEventArgs();

        Assert.Equal("", args.Step);
        Assert.Equal("", args.Detail);
    }

    [Fact]
    public void ProgressEventArgs_SetsFields()
    {
        var args = new ProgressEventArgs
        {
            Step = "handshake",
            Detail = "Performing V3 handshake...",
        };

        Assert.Equal("handshake", args.Step);
        Assert.Equal("Performing V3 handshake...", args.Detail);
    }

    [Fact]
    public void ProgressEventArgs_InheritsFromEventArgs()
    {
        Assert.IsAssignableFrom<EventArgs>(new ProgressEventArgs());
    }

    [Fact]
    public void ConnectionEventArgs_DefaultResult()
    {
        var args = new ConnectionEventArgs();

        Assert.NotNull(args.Result);
        Assert.Equal("", args.Result.SessionId);
    }

    [Fact]
    public void ConnectionEventArgs_SetsResult()
    {
        var result = new ConnectionResult { SessionId = "42" };
        var args = new ConnectionEventArgs { Result = result };

        Assert.Equal("42", args.Result.SessionId);
    }

    [Fact]
    public void ConnectionEventArgs_InheritsFromEventArgs()
    {
        Assert.IsAssignableFrom<EventArgs>(new ConnectionEventArgs());
    }

    [Fact]
    public void DisconnectedEventArgs_DefaultReason()
    {
        var args = new DisconnectedEventArgs();
        Assert.Equal("", args.Reason);
    }

    [Fact]
    public void DisconnectedEventArgs_SetsReason()
    {
        var args = new DisconnectedEventArgs { Reason = "user" };
        Assert.Equal("user", args.Reason);
    }

    [Fact]
    public void DisconnectedEventArgs_InheritsFromEventArgs()
    {
        Assert.IsAssignableFrom<EventArgs>(new DisconnectedEventArgs());
    }

    [Fact]
    public void ErrorEventArgs_SetsException()
    {
        var ex = new InvalidOperationException("test");
        var args = new Sentinel.SDK.Node.ErrorEventArgs { Exception = ex };

        Assert.Same(ex, args.Exception);
    }

    [Fact]
    public void ErrorEventArgs_InheritsFromEventArgs()
    {
        Assert.IsAssignableFrom<EventArgs>(new Sentinel.SDK.Node.ErrorEventArgs { Exception = new Exception() });
    }
}

public class SentinelVpnClientTests
{
    [Fact]
    public void Constructor_NullWallet_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => new SentinelVpnClient(null!));
    }

    [Fact]
    public void IsConnected_ReturnsFalse_WhenNoActiveConnection()
    {
        var wallet = SentinelWallet.Generate();
        using var client = new SentinelVpnClient(wallet);

        Assert.False(client.IsConnected);
    }

    [Fact]
    public void GetStatus_ReturnsNull_WhenNotConnected()
    {
        var wallet = SentinelWallet.Generate();
        using var client = new SentinelVpnClient(wallet);

        Assert.Null(client.GetStatus());
    }

    [Fact]
    public void Dispose_DoesNotThrow_WhenNotConnected()
    {
        var wallet = SentinelWallet.Generate();
        var client = new SentinelVpnClient(wallet);
        client.Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var wallet = SentinelWallet.Generate();
        var client = new SentinelVpnClient(wallet);
        client.Dispose();
        client.Dispose(); // Should not throw
    }

    [Fact]
    public void Implements_IDisposable()
    {
        var wallet = SentinelWallet.Generate();
        using var client = new SentinelVpnClient(wallet);

        Assert.IsAssignableFrom<IDisposable>(client);
    }

    [Fact]
    public void Constructor_AcceptsNullOptions()
    {
        var wallet = SentinelWallet.Generate();
        using var client = new SentinelVpnClient(wallet, null);

        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_AcceptsCustomOptions()
    {
        var wallet = SentinelWallet.Generate();
        var opts = new SentinelVpnOptions { Gigabytes = 3 };
        using var client = new SentinelVpnClient(wallet, opts);

        Assert.NotNull(client);
    }

    [Fact]
    public void HasProgressEvent()
    {
        var eventInfo = typeof(SentinelVpnClient).GetEvent("Progress");
        Assert.NotNull(eventInfo);
    }

    [Fact]
    public void HasConnectedEvent()
    {
        var eventInfo = typeof(SentinelVpnClient).GetEvent("Connected");
        Assert.NotNull(eventInfo);
    }

    [Fact]
    public void HasDisconnectedEvent()
    {
        var eventInfo = typeof(SentinelVpnClient).GetEvent("Disconnected");
        Assert.NotNull(eventInfo);
    }

    [Fact]
    public void HasErrorEvent()
    {
        var eventInfo = typeof(SentinelVpnClient).GetEvent("Error");
        Assert.NotNull(eventInfo);
    }

    [Fact]
    public void HasStaticQuickConnectAsync()
    {
        var method = typeof(SentinelVpnClient).GetMethod("QuickConnectAsync");
        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
    }
}

public class QuickConnectOptionsTests
{
    [Fact]
    public void Defaults_MaxAttempts_IsThree()
    {
        var opts = new QuickConnectOptions();
        Assert.Equal(3, opts.MaxAttempts);
    }

    [Fact]
    public void Defaults_Gigabytes_IsOne()
    {
        var opts = new QuickConnectOptions();
        Assert.Equal(1, opts.Gigabytes);
    }

    [Fact]
    public void Defaults_FullTunnel_IsTrue()
    {
        var opts = new QuickConnectOptions();
        Assert.True(opts.FullTunnel);
    }

    [Fact]
    public void Defaults_SystemProxy_IsTrue()
    {
        var opts = new QuickConnectOptions();
        Assert.True(opts.SystemProxy);
    }

    [Fact]
    public void Defaults_NullableFields_AreNull()
    {
        var opts = new QuickConnectOptions();
        Assert.Null(opts.Countries);
        Assert.Null(opts.ServiceType);
        Assert.Null(opts.LcdUrls);
        Assert.Null(opts.RpcUrls);
        Assert.Null(opts.V2RayExePath);
        Assert.Null(opts.NodePool);
        Assert.Null(opts.Logger);
        Assert.Null(opts.FeeGranter);
    }

    [Fact]
    public void WithInit_SetsAllFields()
    {
        var opts = new QuickConnectOptions
        {
            Countries = new[] { "DE" },
            ServiceType = "wireguard",
            MaxAttempts = 5,
            LcdUrls = new[] { "https://lcd.sentinel.co" },
            RpcUrls = new[] { "https://rpc.sentinel.co" },
            V2RayExePath = @"C:\v2ray\v2ray.exe",
            Gigabytes = 2,
            FullTunnel = false,
            SystemProxy = false,
            NodePool = new[] { "sentnode1abc" },
            FeeGranter = "sent1granter",
        };

        Assert.Equal(new[] { "DE" }, opts.Countries);
        Assert.Equal("wireguard", opts.ServiceType);
        Assert.Equal(5, opts.MaxAttempts);
        Assert.Single(opts.LcdUrls!);
        Assert.Single(opts.RpcUrls!);
        Assert.Equal(@"C:\v2ray\v2ray.exe", opts.V2RayExePath);
        Assert.Equal(2, opts.Gigabytes);
        Assert.False(opts.FullTunnel);
        Assert.False(opts.SystemProxy);
        Assert.Single(opts.NodePool!);
        Assert.Equal("sent1granter", opts.FeeGranter);
    }
}

public class QuickConnectResultTests
{
    [Fact]
    public void Implements_IDisposable()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(QuickConnectResult)));
    }

    [Fact]
    public void Implements_IAsyncDisposable()
    {
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(QuickConnectResult)));
    }

    [Fact]
    public async Task QuickConnectAsync_NullMnemonic_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => SentinelVpnClient.QuickConnectAsync(null!));
    }

    [Fact]
    public async Task QuickConnectAsync_EmptyMnemonic_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => SentinelVpnClient.QuickConnectAsync(""));
    }

    [Fact]
    public async Task QuickConnectAsync_WhitespaceMnemonic_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => SentinelVpnClient.QuickConnectAsync("   "));
    }

    [Fact]
    public async Task QuickConnectAsync_InvalidMnemonic_ThrowsSentinelException()
    {
        await Assert.ThrowsAsync<SentinelException>(
            () => SentinelVpnClient.QuickConnectAsync("not a valid mnemonic phrase at all"));
    }
}
