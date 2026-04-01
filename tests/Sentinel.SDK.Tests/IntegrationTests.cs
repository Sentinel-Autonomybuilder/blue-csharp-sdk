using Sentinel.SDK.Core;
using Sentinel.SDK.Node;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Integration tests against live Sentinel chain.
/// These tests require:
/// - Network access to LCD endpoints
/// - A funded test wallet (set SENTINEL_TEST_MNEMONIC env var)
/// - V2Ray binary in bin/
/// - Admin privileges for WireGuard tests
///
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class IntegrationTests
{
    private static string? TestMnemonic =>
        Environment.GetEnvironmentVariable("SENTINEL_TEST_MNEMONIC");

    [Fact(Skip = "Requires funded wallet - set SENTINEL_TEST_MNEMONIC")]
    public async Task LCD_GetActiveNodes_ReturnsNodes()
    {
        using var client = new ChainClient();
        await client.InitializeAsync();
        var nodes = await client.GetActiveNodesAsync(limit: 5);
        Assert.NotEmpty(nodes);
        Assert.All(nodes, n => Assert.StartsWith("sentnode", n.Address));
    }

    [Fact(Skip = "Requires funded wallet - set SENTINEL_TEST_MNEMONIC")]
    public async Task LCD_GetBalance_ReturnsBalance()
    {
        if (TestMnemonic is null) return;
        using var wallet = SentinelWallet.FromMnemonic(TestMnemonic);
        using var client = new ChainClient();
        await client.InitializeAsync();
        var balance = await client.GetBalanceAsync(wallet.Address);
        Assert.True(balance.Udvpn >= 0);
    }

    [Fact(Skip = "Requires funded wallet - set SENTINEL_TEST_MNEMONIC")]
    public async Task LCD_GetNode_ReturnsSingleNode()
    {
        using var client = new ChainClient();
        await client.InitializeAsync();
        var nodes = await client.GetActiveNodesAsync(limit: 1);
        Assert.NotEmpty(nodes);

        var node = await client.GetNodeAsync(nodes[0].Address);
        Assert.NotNull(node);
        Assert.StartsWith("sentnode", node!.Address);
    }

    [Fact(Skip = "Requires funded wallet - set SENTINEL_TEST_MNEMONIC")]
    public async Task LCD_CheckEndpointHealth_AllEndpointsRespond()
    {
        using var client = new ChainClient();
        await client.InitializeAsync();
        var health = await client.CheckEndpointHealthAsync();
        Assert.NotEmpty(health);
        Assert.Contains(health, h => h.LatencyMs.HasValue);
    }

    [Fact(Skip = "Requires funded wallet - set SENTINEL_TEST_MNEMONIC")]
    public async Task LCD_GetNetworkOverview_ReturnsStats()
    {
        using var client = new ChainClient();
        await client.InitializeAsync();
        var overview = await client.GetNetworkOverviewAsync();
        Assert.True(overview.TotalNodes > 0);
    }

    [Fact(Skip = "Requires funded wallet - set SENTINEL_TEST_MNEMONIC")]
    public void Wallet_FromMnemonic_DerivesSentAddress()
    {
        if (TestMnemonic is null) return;
        using var wallet = SentinelWallet.FromMnemonic(TestMnemonic);
        Assert.StartsWith("sent1", wallet.Address);
    }

    [Fact(Skip = "Requires funded wallet - set SENTINEL_TEST_MNEMONIC")]
    public async Task FullConnection_WireGuard_WorksEndToEnd()
    {
        // The gold standard test:
        // 1. Wallet from mnemonic
        // 2. Balance check
        // 3. Node discovery (find WireGuard node)
        // 4. Session creation (on-chain TX)
        // 5. V3 handshake
        // 6. WireGuard tunnel install
        // 7. Connectivity verification (IP changed)
        // 8. Disconnect + session end
        //
        // Requires: funded wallet, admin privileges, WireGuard installed.
        await Task.CompletedTask; // Placeholder — implement when test wallet is available
    }

    [Fact(Skip = "Requires funded wallet - set SENTINEL_TEST_MNEMONIC")]
    public async Task FullConnection_V2Ray_WorksEndToEnd()
    {
        // Same as WireGuard E2E but for V2Ray:
        // 1. Wallet from mnemonic
        // 2. Balance check
        // 3. Node discovery (find V2Ray node)
        // 4. Session creation (on-chain TX)
        // 5. V3 handshake (verify multi-outbound metadata)
        // 6. V2Ray tunnel start (multi-outbound config)
        // 7. SOCKS5 proxy connectivity check
        // 8. Disconnect + session end
        //
        // Requires: funded wallet, v2ray.exe in bin/.
        await Task.CompletedTask; // Placeholder — implement when test wallet is available
    }

    [Fact(Skip = "Requires funded wallet - set SENTINEL_TEST_MNEMONIC")]
    public async Task Handshake_ChainLagRetry_EventuallySucceeds()
    {
        // Tests the chain propagation delay retry logic:
        // 1. Create session (on-chain TX)
        // 2. Immediately attempt handshake (node may not see session yet)
        // 3. Verify automatic 10s retry on "does not exist" response
        //
        // Requires: funded wallet, active node.
        await Task.CompletedTask; // Placeholder — implement when test wallet is available
    }
}
