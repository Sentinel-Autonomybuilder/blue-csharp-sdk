using Sentinel.SDK.Core;
using Sentinel.SDK.Node;
using Xunit;

namespace Sentinel.SDK.Tests;

public class SentinelVpnServiceTests
{
    private static SentinelWallet CreateWallet() => SentinelWallet.Generate();

    // ─── Constructor ───

    [Fact]
    public void Constructor_NullOperatorWallet_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => new SentinelVpnService(null!));
    }

    [Fact]
    public void Constructor_ValidWallet_DoesNotThrow()
    {
        var wallet = CreateWallet();
        using var service = new SentinelVpnService(wallet);
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_AcceptsNullOptions()
    {
        var wallet = CreateWallet();
        using var service = new SentinelVpnService(wallet, null);
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_AcceptsCustomOptions()
    {
        var wallet = CreateWallet();
        var opts = new SentinelVpnOptions { Gigabytes = 5 };
        using var service = new SentinelVpnService(wallet, options: opts);
        Assert.NotNull(service);
    }

    // ─── User Wallet ───

    [Fact]
    public void HasUserWallet_ReturnsFalse_Initially()
    {
        var wallet = CreateWallet();
        using var service = new SentinelVpnService(wallet);

        Assert.False(service.HasUserWallet);
    }

    [Fact]
    public void UserAddress_IsNull_BeforeSetUserWallet()
    {
        var wallet = CreateWallet();
        using var service = new SentinelVpnService(wallet);

        Assert.Null(service.UserAddress);
    }

    [Fact]
    public void OperatorAddress_MatchesWalletAddress()
    {
        var wallet = CreateWallet();
        using var service = new SentinelVpnService(wallet);

        Assert.Equal(wallet.Address, service.OperatorAddress);
    }

    [Fact]
    public void SetUserWallet_SetsHasUserWallet()
    {
        var operatorWallet = CreateWallet();
        var userWallet = CreateWallet();
        using var service = new SentinelVpnService(operatorWallet);

        service.SetUserWallet(userWallet);

        Assert.True(service.HasUserWallet);
    }

    [Fact]
    public void SetUserWallet_SetsUserAddress()
    {
        var operatorWallet = CreateWallet();
        var userWallet = CreateWallet();
        using var service = new SentinelVpnService(operatorWallet);

        service.SetUserWallet(userWallet);

        Assert.Equal(userWallet.Address, service.UserAddress);
    }

    [Fact]
    public void SetUserWallet_NullUser_ThrowsArgumentNull()
    {
        var wallet = CreateWallet();
        using var service = new SentinelVpnService(wallet);

        Assert.Throws<ArgumentNullException>(() => service.SetUserWallet(null!));
    }

    [Fact]
    public void SetUserWallet_CanBeCalledMultipleTimes()
    {
        var operatorWallet = CreateWallet();
        var user1 = CreateWallet();
        var user2 = CreateWallet();
        using var service = new SentinelVpnService(operatorWallet);

        service.SetUserWallet(user1);
        Assert.Equal(user1.Address, service.UserAddress);

        service.SetUserWallet(user2);
        Assert.Equal(user2.Address, service.UserAddress);
    }

    // ─── Connection State ───

    [Fact]
    public void IsConnected_ReturnsFalse_Initially()
    {
        var wallet = CreateWallet();
        using var service = new SentinelVpnService(wallet);

        Assert.False(service.IsConnected);
    }

    [Fact]
    public void GetStatus_ReturnsNull_Initially()
    {
        var wallet = CreateWallet();
        using var service = new SentinelVpnService(wallet);

        Assert.Null(service.GetStatus());
    }

    // ─── Dispose ───

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var wallet = CreateWallet();
        var service = new SentinelVpnService(wallet);
        service.Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var wallet = CreateWallet();
        var service = new SentinelVpnService(wallet);
        service.Dispose();
        service.Dispose(); // Should not throw
    }

    [Fact]
    public void Implements_IDisposable()
    {
        var wallet = CreateWallet();
        using var service = new SentinelVpnService(wallet);

        Assert.IsAssignableFrom<IDisposable>(service);
    }

    // ─── Events ───

    [Fact]
    public void HasProgressEvent()
    {
        var eventInfo = typeof(SentinelVpnService).GetEvent("Progress");
        Assert.NotNull(eventInfo);
    }

    [Fact]
    public void HasConnectedEvent()
    {
        var eventInfo = typeof(SentinelVpnService).GetEvent("Connected");
        Assert.NotNull(eventInfo);
    }

    [Fact]
    public void HasDisconnectedEvent()
    {
        var eventInfo = typeof(SentinelVpnService).GetEvent("Disconnected");
        Assert.NotNull(eventInfo);
    }

    [Fact]
    public void HasErrorEvent()
    {
        var eventInfo = typeof(SentinelVpnService).GetEvent("Error");
        Assert.NotNull(eventInfo);
    }

    // ─── Dispose Prevents Operations ───

    [Fact]
    public void GetStatus_ThrowsAfterDispose()
    {
        var wallet = CreateWallet();
        var service = new SentinelVpnService(wallet);
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.GetStatus());
    }

    [Fact]
    public void SetUserWallet_ThrowsAfterDispose()
    {
        var wallet = CreateWallet();
        var service = new SentinelVpnService(wallet);
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.SetUserWallet(CreateWallet()));
    }
}
