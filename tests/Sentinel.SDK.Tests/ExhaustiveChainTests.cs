using Sentinel.SDK.Core;
using Sentinel.SDK.Node;
using Xunit;
using Xunit.Abstractions;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Exhaustive on-chain operations test — matches JS test-all-chain-ops.js exactly.
/// Tests EVERY blockchain function: provider, plan, lease, subscription, session,
/// fee grant, transfer, queries, and VPN connection.
/// Cost: ~10-15 P2P per run.
/// </summary>
[TestCaseOrderer("Sentinel.SDK.Tests.PriorityOrderer", "Sentinel.SDK.Tests")]
public class ExhaustiveChainTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private SentinelWallet? _opWallet;
    private ChainClient? _chain;
    private TransactionBuilder? _tx;
    private string _provAddr = "";

    // NOTE: Plan 44 was deactivated by exhaustive tests. Use a fresh active plan.
    // The S01 and C01 tests create their own plans to avoid this issue.
    private const int PLAN_ID = 44; // For query tests only (may be inactive)
    private const string NODE = "sentnode1qny8deh2e23g793jhqz0ky7umunxud7p2f477p";

    public ExhaustiveChainTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        var envPath = @"C:\Users\Connect\Desktop\sentinel-node-tester\.env";
        if (!File.Exists(envPath)) return;
        var mnemonic = File.ReadAllLines(envPath)
            .FirstOrDefault(l => l.StartsWith("MNEMONIC="))
            ?["MNEMONIC=".Length..]
            .Trim('"', '\'', ' ');
        if (mnemonic == null) return;

        _opWallet = SentinelWallet.FromMnemonic(mnemonic);
        _chain = new ChainClient(logger: new NullSdkLogger());
        await _chain.InitializeAsync();
        _tx = new TransactionBuilder(_opWallet, _chain);
        _provAddr = SentinelWallet.SentToSentprov(_opWallet.Address);
        _out.WriteLine($"Operator: {_opWallet.Address} | Provider: {_provAddr}");
    }

    public Task DisposeAsync()
    {
        _chain?.Dispose();
        _opWallet?.Dispose();
        return Task.CompletedTask;
    }

    private void Skip()
    {
        if (_opWallet == null) _out.WriteLine("SKIP: No mnemonic");
    }

    private async Task TxWait() => await Task.Delay(7000);

    // ═══ QUERIES ═══

    [Fact]
    public async Task Q01_FetchAllNodes()
    {
        Skip(); if (_chain == null) return;
        var nodes = await _chain.GetActiveNodesAsync(limit: 5000);
        _out.WriteLine($"Nodes: {nodes.Count}");
        Assert.True(nodes.Count > 900);
    }

    [Fact]
    public async Task Q02_GetSingleNode()
    {
        Skip(); if (_chain == null) return;
        var node = await _chain.GetNodeAsync(NODE);
        Assert.NotNull(node);
        _out.WriteLine($"Node: {node!.Address} GB prices: {node.GigabytePrices.Length}");
    }

    [Fact]
    public async Task Q03_GetBalance()
    {
        Skip(); if (_chain == null) return;
        var bal = await _chain.GetBalanceAsync(_opWallet!.Address);
        _out.WriteLine($"Balance: {Helpers.FormatP2P(bal.Udvpn)}");
        Assert.True(bal.Udvpn > 0);
    }

    [Fact]
    public async Task Q04_NetworkOverview()
    {
        Skip(); if (_chain == null) return;
        var ov = await _chain.GetNetworkOverviewAsync();
        _out.WriteLine($"Nodes: {ov.TotalNodes} Countries: {ov.ByCountry.Count}");
        Assert.True(ov.TotalNodes > 900);
    }

    [Fact]
    public async Task Q05_EndpointHealth()
    {
        Skip(); if (_chain == null) return;
        var health = await _chain.CheckEndpointHealthAsync();
        var reachable = health.Count(h => h.LatencyMs.HasValue);
        _out.WriteLine($"Reachable: {reachable}/{health.Count}");
        Assert.True(reachable > 0);
    }

    [Fact]
    public async Task Q06_DiscoverPlans()
    {
        Skip(); if (_chain == null) return;
        var plans = await _chain.DiscoverPlansAsync(maxId: 50);
        _out.WriteLine($"Plans: {plans.Count}");
        Assert.True(plans.Count > 0);
    }

    [Fact]
    public async Task Q07_PlanNodes()
    {
        Skip(); if (_chain == null) return;
        var nodes = await _chain.GetPlanNodesAsync(PLAN_ID);
        _out.WriteLine($"Plan {PLAN_ID} nodes: {nodes.Count}");
        // Plan node linkage is chain state — may be 0 if nodes were unlinked
        // Test validates the query works, not that a specific plan has nodes
        Assert.NotNull(nodes);
    }

    [Fact]
    public async Task Q08_NodeStatus()
    {
        Skip(); if (_chain == null) return;
        var node = await _chain.GetNodeAsync(NODE);
        Assert.NotNull(node);
        var rawUrl = node!.RemoteAddrs.FirstOrDefault() ?? node.RemoteUrl!;
        if (!rawUrl.StartsWith("http")) rawUrl = "https://" + rawUrl;
        var status = await NodeClient.GetStatusAsync(rawUrl, null, NODE);
        _out.WriteLine($"Node: {status.Moniker} ({status.Type}) {status.Location.Country}");
        Assert.Equal("wireguard", status.Type);
    }

    [Fact]
    public async Task Q09_Sessions()
    {
        Skip(); if (_chain == null) return;
        var sessions = await _chain.GetSessionsAsync(_opWallet!.Address);
        _out.WriteLine($"Sessions: {sessions.Count}");
        // May be 0 if no active sessions — that's fine
    }

    [Fact]
    public async Task Q10_Subscriptions()
    {
        Skip(); if (_chain == null) return;
        var subs = await _chain.GetSubscriptionsAsync(_opWallet!.Address);
        _out.WriteLine($"Subscriptions: {subs.Count}");
    }

    [Fact]
    public async Task Q11_HasActiveSubscription()
    {
        Skip(); if (_chain == null) return;
        var has = await _chain.HasActiveSubscriptionAsync(_opWallet!.Address, PLAN_ID);
        _out.WriteLine($"Has subscription to plan {PLAN_ID}: {has}");
    }

    [Fact]
    public async Task Q12_FeeGrants()
    {
        Skip(); if (_chain == null) return;
        var grants = await _chain.QueryFeeGrantsAsync(_opWallet!.Address);
        _out.WriteLine($"Fee grants received: {grants.Count}");
    }

    // ═══ PROVIDER TX ═══

    [Fact]
    public async Task P01_UpdateProviderDetails()
    {
        Skip(); if (_tx == null) return;
        var msg = MessageBuilder.UpdateProviderDetails(
            _provAddr, "SDK Test Provider v3", "", "https://sentinel.co", "C# exhaustive test");
        var r = await _tx.BroadcastAsync(msg);
        _out.WriteLine($"TX: {r.TxHash} Code: {r.Code}");
        Assert.True(r.Success, r.RawLog);
    }

    [Fact]
    public async Task P02_UpdateProviderStatus()
    {
        Skip(); if (_tx == null) return;
        await TxWait();
        // Provider status requires sentprov prefix
        var msg = MessageBuilder.UpdateProviderStatus(_provAddr, 1); // 1 = active
        var r = await _tx.BroadcastAsync(msg);
        _out.WriteLine($"TX: {r.TxHash} Code: {r.Code}");
        Assert.True(r.Success, r.RawLog);
    }

    // ═══ PLAN TX ═══

    [Fact]
    public async Task PL01_CreatePlan()
    {
        Skip(); if (_tx == null) return;
        await TxWait();
        var msg = MessageBuilder.CreatePlan(
            _provAddr,
            bytes: "500000000", // 500 MB
            durationSeconds: 7 * 24 * 3600, // 7 days
            prices: new[] { new PriceEntry("udvpn", "0.000000500000000000", "500000") });
        var r = await _tx.BroadcastAsync(msg);
        _out.WriteLine($"TX: {r.TxHash} Code: {r.Code}");
        Assert.True(r.Success, r.RawLog);
    }

    [Fact]
    public async Task PL02_ActivatePlan()
    {
        Skip(); if (_tx == null || _chain == null) return;
        await TxWait();
        // Find our newest plan
        var plans = await _chain.DiscoverPlansAsync(maxId: 60);
        var latest = plans.OrderByDescending(p => p.Id).FirstOrDefault();
        if (latest == null) { _out.WriteLine("No plans found"); return; }
        _out.WriteLine($"Activating plan {latest.Id}");
        var msg = MessageBuilder.UpdatePlanStatus(_provAddr, (ulong)latest.Id, 1);
        var r = await _tx.BroadcastAsync(msg);
        _out.WriteLine($"TX: {r.TxHash} Code: {r.Code}");
        Assert.True(r.Success, r.RawLog);
    }

    [Fact]
    public async Task PL03_LinkNode()
    {
        Skip(); if (_tx == null || _chain == null) return;
        await TxWait();
        var plans = await _chain.DiscoverPlansAsync(maxId: 60);
        var latest = plans.OrderByDescending(p => p.Id).FirstOrDefault();
        if (latest == null) return;
        try
        {
            var msg = MessageBuilder.LinkNode(_provAddr, (ulong)latest.Id, NODE);
            var r = await _tx.BroadcastAsync(msg);
            _out.WriteLine($"Linked to plan {latest.Id}. TX: {r.TxHash}");
            Assert.True(r.Success, r.RawLog);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate") || ex.Message.Contains("already"))
        {
            _out.WriteLine("Already linked (OK)");
        }
    }

    [Fact]
    public async Task PL04_UnlinkNode()
    {
        Skip(); if (_tx == null || _chain == null) return;
        await TxWait();
        var plans = await _chain.DiscoverPlansAsync(maxId: 60);
        var latest = plans.OrderByDescending(p => p.Id).FirstOrDefault();
        if (latest == null) return;
        try
        {
            var msg = MessageBuilder.UnlinkNode(_provAddr, (ulong)latest.Id, NODE);
            var r = await _tx.BroadcastAsync(msg);
            _out.WriteLine($"Unlinked from plan {latest.Id}. TX: {r.TxHash}");
            Assert.True(r.Success, r.RawLog);
        }
        catch (Exception ex) when (ex.Message.Contains("not found"))
        {
            _out.WriteLine("Not linked (OK — may have been unlinked already)");
        }
    }

    [Fact]
    public async Task PL05_DeactivatePlan()
    {
        Skip(); if (_tx == null || _chain == null) return;
        await TxWait();
        var plans = await _chain.DiscoverPlansAsync(maxId: 60);
        var latest = plans.OrderByDescending(p => p.Id).FirstOrDefault();
        if (latest == null) return;
        // Status 3 = INACTIVE (NOT 2 which is INACTIVE_PENDING — internal only)
        var msg = MessageBuilder.UpdatePlanStatus(_provAddr, (ulong)latest.Id, 3);
        var r = await _tx.BroadcastAsync(msg);
        _out.WriteLine($"Deactivated plan {latest.Id}. TX: {r.TxHash} Code: {r.Code}");
        Assert.True(r.Success, r.RawLog);
    }

    // ═══ WALLET & TRANSFER ═══

    [Fact]
    public async Task W01_SendTokens()
    {
        Skip(); if (_tx == null) return;
        await TxWait();
        using var userWallet = SentinelWallet.Generate();
        _out.WriteLine($"User: {userWallet.Address}");
        var msg = MessageBuilder.Send(_opWallet!.Address, userWallet.Address, 1_000_000); // 1 P2P
        var r = await _tx.BroadcastAsync(msg);
        _out.WriteLine($"Sent 1 P2P. TX: {r.TxHash} Code: {r.Code}");
        Assert.True(r.Success, r.RawLog);
    }

    // ═══ FEE GRANT LIFECYCLE ═══

    [Fact]
    public async Task FG01_GrantFeeAllowance()
    {
        Skip(); if (_tx == null) return;
        await TxWait();
        using var user = SentinelWallet.Generate();
        _out.WriteLine($"Granting to: {user.Address}");
        var msg = MessageBuilder.GrantFeeAllowance(_opWallet!.Address, user.Address, 5_000_000);
        var r = await _tx.BroadcastAsync(msg);
        _out.WriteLine($"TX: {r.TxHash} Code: {r.Code}");
        Assert.True(r.Success, r.RawLog);

        // Wait and verify
        await Task.Delay(8000);
        var grants = await _chain!.QueryFeeGrantsAsync(user.Address);
        _out.WriteLine($"Grants after issue: {grants.Count}");
        Assert.True(grants.Count > 0);
    }

    [Fact]
    public async Task FG02_RevokeFeeAllowance()
    {
        Skip(); if (_tx == null) return;
        await TxWait();
        // Grant first
        using var user = SentinelWallet.Generate();
        var grantMsg = MessageBuilder.GrantFeeAllowance(_opWallet!.Address, user.Address, 1_000_000);
        await _tx.BroadcastAsync(grantMsg);
        await Task.Delay(12000);

        // Revoke
        var revokeMsg = MessageBuilder.RevokeFeeAllowance(_opWallet.Address, user.Address);
        var r = await _tx.BroadcastAsync(revokeMsg);
        _out.WriteLine($"Revoke TX: {r.TxHash} Code: {r.Code}");
        Assert.True(r.Success, r.RawLog);

        // Verify revoked
        await Task.Delay(12000);
        var grants = await _chain!.QueryFeeGrantsAsync(user.Address);
        _out.WriteLine($"Grants after revoke: {grants.Count}");
        Assert.Equal(0, grants.Count);
    }

    // ═══ SUBSCRIPTION ═══

    [Fact]
    public async Task S01_SubscribeToPlan()
    {
        Skip(); if (_tx == null || _chain == null) return;
        await TxWait();

        // Create a fresh active plan (Plan 44 was deactivated by previous test runs)
        var planMsg = MessageBuilder.CreatePlan(_provAddr, "100000000", 3600,
            new[] { new PriceEntry("udvpn", "0.000000100000000000", "100000") });
        var planR = await _tx.BroadcastAsync(planMsg);
        Assert.True(planR.Success, $"Plan create failed: {planR.RawLog}");
        _out.WriteLine($"Plan created: {planR.TxHash}");
        await TxWait();

        // Find the new plan ID from chain
        var plans = await _chain.DiscoverPlansAsync(maxId: 70);
        var freshPlan = plans.OrderByDescending(p => p.Id).FirstOrDefault();
        Assert.NotNull(freshPlan);
        var freshPlanId = freshPlan!.Id;
        _out.WriteLine($"Fresh plan ID: {freshPlanId}");

        // Activate
        var activateMsg = MessageBuilder.UpdatePlanStatus(_provAddr, (ulong)freshPlanId, 1);
        await _tx.BroadcastAsync(activateMsg);
        await TxWait();

        // Link node
        try
        {
            // Lease first
            var node = await _chain.GetNodeAsync(NODE);
            if (node?.HourlyPrices.Length > 0)
            {
                var hrPrice = node.HourlyPrices.First(p => p.Denom == "udvpn");
                var leaseMsg = MessageBuilder.StartLease(_provAddr, NODE, 1, hrPrice);
                try { await _tx.BroadcastAsync(leaseMsg); } catch { }
                await TxWait();
            }
            var linkMsg = MessageBuilder.LinkNode(_provAddr, (ulong)freshPlanId, NODE);
            await _tx.BroadcastAsync(linkMsg);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate") || ex.Message.Contains("already"))
        {
            _out.WriteLine("Already linked");
        }
        await TxWait();

        // Create user + fund
        using var user = SentinelWallet.Generate();
        var sendMsg = MessageBuilder.Send(_opWallet!.Address, user.Address, 3_000_000);
        await _tx.BroadcastAsync(sendMsg);
        await Task.Delay(10000);

        // Subscribe
        using var userChain = new ChainClient(logger: new NullSdkLogger());
        await userChain.InitializeAsync();
        var userTx = new TransactionBuilder(user, userChain);
        var subMsg = MessageBuilder.StartSubscription(user.Address, (ulong)freshPlanId);
        var r = await userTx.BroadcastAsync(subMsg);
        _out.WriteLine($"Subscribe TX: {r.TxHash} Code: {r.Code}");
        Assert.True(r.Success, r.RawLog);

        // Verify
        await Task.Delay(8000);
        var has = await userChain.HasActiveSubscriptionAsync(user.Address, freshPlanId);
        _out.WriteLine($"Has subscription to plan {freshPlanId}: {has}");
        Assert.True(has);
    }

    // ═══ FULL PLAN CONNECTION (fee-granted WireGuard) ═══

    [Fact]
    public async Task C01_PlanConnectionFeeGranted()
    {
        Skip(); if (_tx == null || _chain == null) return;
        await TxWait();

        // 0. Create fresh active plan (Plan 44 deactivated by previous runs)
        var planMsg = MessageBuilder.CreatePlan(_provAddr, "100000000", 3600,
            new[] { new PriceEntry("udvpn", "0.000000100000000000", "100000") });
        var planR = await _tx.BroadcastAsync(planMsg);
        Assert.True(planR.Success, planR.RawLog);
        await TxWait();

        var plans = await _chain.DiscoverPlansAsync(maxId: 80);
        var freshPlan = plans.OrderByDescending(p => p.Id).FirstOrDefault();
        Assert.NotNull(freshPlan);
        var connPlanId = freshPlan!.Id;
        _out.WriteLine($"Plan: {connPlanId}");

        // Activate + lease + link
        await _tx.BroadcastAsync(MessageBuilder.UpdatePlanStatus(_provAddr, (ulong)connPlanId, 1));
        await TxWait();
        var node = await _chain.GetNodeAsync(NODE);
        if (node?.HourlyPrices.Length > 0)
        {
            try { await _tx.BroadcastAsync(MessageBuilder.StartLease(_provAddr, NODE, 1, node.HourlyPrices.First(p => p.Denom == "udvpn"))); } catch { }
            await TxWait();
        }
        try { await _tx.BroadcastAsync(MessageBuilder.LinkNode(_provAddr, (ulong)connPlanId, NODE)); } catch { }
        await TxWait();

        // 1. Create user + fund
        using var user = SentinelWallet.Generate();
        _out.WriteLine($"User: {user.Address}");
        await _tx.BroadcastAsync(MessageBuilder.Send(_opWallet!.Address, user.Address, 3_000_000));
        await Task.Delay(10000);

        // 2. Subscribe
        using var uChain = new ChainClient(logger: new NullSdkLogger());
        await uChain.InitializeAsync();
        var uTx = new TransactionBuilder(user, uChain);
        await uTx.BroadcastAsync(MessageBuilder.StartSubscription(user.Address, (ulong)connPlanId));
        await Task.Delay(10000);

        // 3. Fee grant
        await _tx.BroadcastAsync(MessageBuilder.GrantFeeAllowance(_opWallet.Address, user.Address, 5_000_000));
        await Task.Delay(10000);

        // 4. Get subscription ID (retry once if LCD is behind)
        var subs = await uChain.GetSubscriptionsAsync(user.Address);
        var sub = subs.FirstOrDefault(s => s.PlanId == connPlanId.ToString() && s.Status.Contains("active"));
        if (sub == null)
        {
            _out.WriteLine("Subscription not found yet, retrying after 10s...");
            await Task.Delay(10000);
            subs = await uChain.GetSubscriptionsAsync(user.Address);
            sub = subs.FirstOrDefault(s => s.PlanId == connPlanId.ToString() && s.Status.Contains("active"));
        }
        Assert.NotNull(sub);
        var subId = ulong.Parse(sub!.Id);
        _out.WriteLine($"Subscription: {subId}");

        // 5. Connect via subscription with fee grant
        var vpn = new SentinelVpnClient(user, new SentinelVpnOptions
        {
            FullTunnel = true,
            Dns = "handshake",
            FeeGranter = _opWallet.Address,
            Logger = new NullSdkLogger(),
        });

        try
        {
            vpn.Progress += (_, e) => _out.WriteLine($"  [{e.Step}] {e.Detail}");
            var conn = await vpn.ConnectViaSubscriptionAsync(subId, NODE);
            _out.WriteLine($"CONNECTED! Session: {conn.SessionId} Type: {conn.ServiceType}");
            Assert.Equal("wireguard", conn.ServiceType);
            Assert.False(string.IsNullOrEmpty(conn.SessionId));

            await vpn.DisconnectAsync();
            _out.WriteLine("Disconnected");
        }
        finally
        {
            await vpn.DisposeAsync();
        }
    }

    // ═══ HELPERS (pure logic, no chain) ═══

    [Fact]
    public void H01_CountryMap() => Assert.Equal("NL", Constants.CountryNameToCode("The Netherlands"));

    [Fact]
    public void H02_CountryVariants()
    {
        Assert.Equal("TR", Constants.CountryNameToCode("Türkiye"));
        Assert.Equal("CD", Constants.CountryNameToCode("DR Congo"));
        Assert.Equal("CZ", Constants.CountryNameToCode("Czechia"));
        Assert.Equal("RU", Constants.CountryNameToCode("Russian Federation"));
        Assert.Equal("VN", Constants.CountryNameToCode("Viet Nam"));
        Assert.Equal("KR", Constants.CountryNameToCode("South Korea"));
        Assert.Equal("AE", Constants.CountryNameToCode("UAE"));
        Assert.Null(Constants.CountryNameToCode("Atlantis"));
    }

    [Fact]
    public void H03_FlagUrl() => Assert.Contains("flagcdn.com", Constants.GetFlagUrl("US"));

    [Fact]
    public void H04_DnsPresets()
    {
        var hns = Constants.DnsPresets.Resolve("handshake");
        Assert.Contains("103.196.38.38", hns);
        Assert.Contains("8.8.8.8", hns); // fallback
    }

    [Fact]
    public void H05_UserMessages()
    {
        var codes = new[] { "INSUFFICIENT_BALANCE", "NODE_OFFLINE", "V2RAY_ALL_FAILED",
            "WG_NOT_AVAILABLE", "TLS_CERT_CHANGED", "INVALID_MNEMONIC", "ABORTED",
            "CHAIN_LAG", "NODE_DATABASE_CORRUPT", "INVALID_ASSIGNED_IP" };
        foreach (var code in codes)
        {
            var msg = Helpers.UserMessage(code);
            Assert.NotEqual("An unexpected error occurred.", msg);
        }
    }

    [Fact]
    public void H06_EstimatePrice()
    {
        var node = new ChainNode("sentnode1test", ["1.2.3.4:8585"], "1.2.3.4:8585",
            [new PriceEntry("udvpn", "0.000040152030000000", "40152030")],
            [new PriceEntry("udvpn", "0.000033409250000000", "33409250")], 1);
        var gb = Helpers.EstimateSessionPrice(node, "gb", 5);
        Assert.True(gb.CostUdvpn > 0);
        var hr = Helpers.EstimateSessionPrice(node, "hour", 4);
        Assert.True(hr.CostUdvpn > 0);
    }

    [Fact]
    public void H07_SessionAllocation()
    {
        var session = new ChainSession("1", "sent1a", "sentnode1b",
            "500000000", "100000000", "1000000000",
            "44.7s", "0s", "active", null, null);
        var alloc = Helpers.ComputeSessionAllocation(session);
        Assert.Equal(60.0, alloc.UsedPercent);
        Assert.True(alloc.IsGbBased);
    }

    [Fact]
    public void H08_PriceEntryDisplay()
    {
        var entry = new PriceEntry("udvpn", "0.000040152030000000", "40152030");
        Assert.Equal(40152030, entry.UdvpnAmount);
        Assert.Contains("P2P", entry.DisplayPrice);
    }

    [Fact]
    public void H09_AppTypes()
    {
        Assert.Equal(3, Constants.AppTypes.All.Length);
        Assert.Equal("white_label", Constants.AppTypes.WhiteLabel);
        Assert.Equal("direct_p2p", Constants.AppTypes.DirectP2P);
    }

    [Fact]
    public void H10_GbHourOptions()
    {
        Assert.Contains(1, Constants.GbOptions);
        Assert.Contains(50, Constants.GbOptions);
        Assert.Contains(24, Constants.HourOptions);
    }
}
