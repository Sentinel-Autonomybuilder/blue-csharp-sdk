using Sentinel.SDK.Core;
using Sentinel.SDK.Node;
using Xunit;
using Xunit.Abstractions;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Full plan lifecycle on live mainnet:
/// 1. Use existing Plan #44 (created by JS test)
/// 2. Generate new user wallet
/// 3. Transfer P2P from operator to user
/// 4. Subscribe user to plan
/// 5. Issue fee grant
/// 6. Connect via plan with fee grant
/// 7. Verify tunnel works
/// 8. Disconnect
/// </summary>
public class PlanLifecycleTests
{
    private readonly ITestOutputHelper _output;

    public PlanLifecycleTests(ITestOutputHelper output) => _output = output;

    private static string? GetMnemonic()
    {
        var envPath = @"C:\Users\Connect\Desktop\sentinel-node-tester\.env";
        if (!File.Exists(envPath)) return null;
        foreach (var line in File.ReadAllLines(envPath))
        {
            if (line.StartsWith("MNEMONIC="))
                return line["MNEMONIC=".Length..].Trim('"', '\'', ' ');
        }
        return null;
    }

    [Fact]
    public async Task FullPlanLifecycle_SubscribeGrantConnect()
    {
        var opMnemonic = GetMnemonic();
        if (opMnemonic is null) { _output.WriteLine("SKIP: No mnemonic"); return; }

        const int PLAN_ID = 44;
        const string NODE = "sentnode1qqywpumwtxxgffqqr9eg94w72tlragzjg0zxs4";

        // Verify plan has this node linked — skip if chain state has changed
        try
        {
            using var preHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var planResp = await preHttp.GetStringAsync($"https://lcd.sentinel.co/sentinel/node/v3/plans/{PLAN_ID}/nodes?status=1");
            if (!planResp.Contains(NODE))
            {
                _output.WriteLine($"SKIP: Node {NODE} not linked to plan {PLAN_ID} — chain state changed");
                return;
            }
        }
        catch
        {
            _output.WriteLine($"SKIP: Could not verify plan {PLAN_ID} node linkage (LCD unavailable or plan query not implemented)");
            return;
        }

        // ─── 1. Operator wallet ───
        using var opWallet = SentinelWallet.FromMnemonic(opMnemonic);
        using var opChain = new ChainClient(logger: new NullSdkLogger());
        await opChain.InitializeAsync();
        var opTx = new TransactionBuilder(opWallet, opChain);

        var opBal = await opChain.GetBalanceAsync(opWallet.Address);
        _output.WriteLine($"1. Operator: {opWallet.Address} | Balance: {Helpers.FormatP2P(opBal.Udvpn)}");
        Assert.True(opBal.Udvpn > 5_000_000, "Operator needs at least 5 P2P");

        // ─── 2. Generate user wallet ───
        using var userWallet = SentinelWallet.Generate();
        _output.WriteLine($"2. User: {userWallet.Address}");

        // ─── 3. Transfer 3 P2P to user ───
        var sendMsg = MessageBuilder.Send(opWallet.Address, userWallet.Address, 3_000_000);
        var sendResult = await opTx.BroadcastAsync(sendMsg);
        _output.WriteLine($"3. Transfer: {sendResult.TxHash} Code: {sendResult.Code}");
        Assert.True(sendResult.Success, $"Transfer failed: {sendResult.RawLog}");

        await Task.Delay(10000); // Chain propagation — LCD may lag 1-2 blocks

        // Verify user balance (retry once if LCD is behind)
        var userBal = await opChain.GetBalanceAsync(userWallet.Address);
        if (userBal.Udvpn == 0)
        {
            _output.WriteLine("   Balance still 0, retrying after 10s...");
            await Task.Delay(10000);
            userBal = await opChain.GetBalanceAsync(userWallet.Address);
        }
        _output.WriteLine($"   User balance: {Helpers.FormatP2P(userBal.Udvpn)}");
        Assert.True(userBal.Udvpn >= 2_000_000, $"Expected >=2M udvpn, got {userBal.Udvpn}");

        // ─── 4. Subscribe user to plan ───
        using var userChain = new ChainClient(logger: new NullSdkLogger());
        await userChain.InitializeAsync();
        var userTx = new TransactionBuilder(userWallet, userChain);
        // Note: SentinelVpnClient needs mnemonic string for reconnection.
        // For this test, we use the wallet's exported mnemonic.
        var userMnemonic = userWallet.ExportMnemonicString();

        var subMsg = MessageBuilder.StartSubscription(userWallet.Address, (ulong)PLAN_ID);
        var subResult = await userTx.BroadcastAsync(subMsg);
        _output.WriteLine($"4. Subscribe: {subResult.TxHash} Code: {subResult.Code}");
        Assert.True(subResult.Success, $"Subscribe failed: {subResult.RawLog}");

        await Task.Delay(5000);

        // Verify subscription
        var hasSub = await userChain.HasActiveSubscriptionAsync(userWallet.Address, PLAN_ID);
        _output.WriteLine($"   Has subscription: {hasSub}");
        Assert.True(hasSub);

        // ─── 5. Fee grant from operator to user ───
        var grantMsg = MessageBuilder.GrantFeeAllowance(opWallet.Address, userWallet.Address, 5_000_000);
        var grantResult = await opTx.BroadcastAsync(grantMsg);
        _output.WriteLine($"5. Fee grant: {grantResult.TxHash} Code: {grantResult.Code}");
        Assert.True(grantResult.Success, $"Grant failed: {grantResult.RawLog}");

        await Task.Delay(8000); // LCD needs more time to index fee grants

        // Verify fee grant
        var grants = await opChain.QueryFeeGrantsAsync(userWallet.Address);
        _output.WriteLine($"   Grants: {grants.Count}");
        if (grants.Count == 0)
        {
            _output.WriteLine("   Retrying grant query after 10s...");
            await Task.Delay(10000);
            grants = await opChain.QueryFeeGrantsAsync(userWallet.Address);
            _output.WriteLine($"   Grants (retry): {grants.Count}");
        }
        Assert.True(grants.Count > 0, "Fee grant not found on chain after 18s");

        // ─── 6. Get subscription ID ───
        var subs = await userChain.GetSubscriptionsAsync(userWallet.Address);
        var activeSub = subs.FirstOrDefault(s => s.PlanId == PLAN_ID.ToString() && s.Status.Contains("active"));
        Assert.NotNull(activeSub);
        var subscriptionId = ulong.Parse(activeSub!.Id);
        _output.WriteLine($"6. Subscription ID: {subscriptionId}");

        // ─── 7. Connect via subscription with fee grant ───
        _output.WriteLine($"7. Connecting via Subscription #{subscriptionId} to {NODE}...");

        var vpnOptions = new SentinelVpnOptions
        {
            FullTunnel = true,
            Dns = "handshake",
            FeeGranter = opWallet.Address,
            Logger = new TestSdkLogger(_output),
        };

        using var vpn = new SentinelVpnClient(userWallet, vpnOptions);
        vpn.Progress += (_, e) => _output.WriteLine($"   [{e.Step}] {e.Detail}");

        try
        {
            var conn = await vpn.ConnectViaSubscriptionAsync(subscriptionId, NODE);
            _output.WriteLine($"   ✓ CONNECTED! Session: {conn.SessionId} Type: {conn.ServiceType}");

            // ─── 7. Verify ───
            Assert.Equal("wireguard", conn.ServiceType);
            Assert.False(string.IsNullOrEmpty(conn.SessionId));

            // Check user balance — fee grant should have covered gas
            // Retry once on LCD failure (known flaky endpoint issue)
            Balance userBalAfter;
            try
            {
                userBalAfter = await userChain.GetBalanceAsync(userWallet.Address);
            }
            catch
            {
                _output.WriteLine($"   Balance check failed, retrying after 5s...");
                await Task.Delay(5000);
                userBalAfter = await userChain.GetBalanceAsync(userWallet.Address);
            }
            _output.WriteLine($"   User balance after: {Helpers.FormatP2P(userBalAfter.Udvpn)}");

            // ─── 8. Disconnect ───
            await vpn.DisconnectAsync();
            _output.WriteLine($"   Disconnected");

            _output.WriteLine($"\n═══ C# PLAN LIFECYCLE TEST PASSED ═══");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"   ✗ CONNECT FAILED: {ex.Message}");
            try { await vpn.DisconnectAsync(); } catch { }
            throw;
        }
    }

    private class TestSdkLogger : ISdkLogger
    {
        private readonly ITestOutputHelper _out;
        public TestSdkLogger(ITestOutputHelper o) => _out = o;
        public void Debug(string message) => _out.WriteLine($"   [SDK DBG] {message}");
        public void Info(string message) => _out.WriteLine($"   [SDK] {message}");
        public void Warn(string message) => _out.WriteLine($"   [SDK WARN] {message}");
        public void Error(string message, Exception? ex = null) => _out.WriteLine($"   [SDK ERROR] {message} {ex?.Message}");
    }
}
