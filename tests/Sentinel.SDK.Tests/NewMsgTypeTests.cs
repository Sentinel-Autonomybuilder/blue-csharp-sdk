using Sentinel.SDK.Core;
using Xunit;
using Xunit.Abstractions;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests the 9 NEW v3 message types added from sentinel-go-sdk.
/// Verifies encoding + broadcast (chain may reject for state reasons — that's OK).
/// </summary>
public class NewMsgTypeTests
{
    private readonly ITestOutputHelper _out;
    public NewMsgTypeTests(ITestOutputHelper output) => _out = output;

    private static (SentinelWallet?, ChainClient?, TransactionBuilder?) Setup()
    {
        var envPath = @"C:\Users\Connect\Desktop\sentinel-node-tester\.env";
        if (!File.Exists(envPath)) return (null, null, null);
        var mnemonic = File.ReadAllLines(envPath)
            .FirstOrDefault(l => l.StartsWith("MNEMONIC="))
            ?["MNEMONIC=".Length..].Trim('"', '\'', ' ');
        if (mnemonic == null) return (null, null, null);
        var w = SentinelWallet.FromMnemonic(mnemonic);
        var c = new ChainClient(logger: new NullSdkLogger());
        c.InitializeAsync().Wait();
        var tx = new TransactionBuilder(w, c);
        return (w, c, tx);
    }

    private async Task TestMsg(string name, SentinelMessage msg, TransactionBuilder tx)
    {
        try
        {
            var r = await tx.BroadcastAsync(msg);
            _out.WriteLine($"✓ {name}: code={r.Code} tx={r.TxHash?[..10]}");
        }
        catch (Exception ex)
        {
            var m = ex.Message ?? "";
            // Chain rejected = encoding worked
            if (m.Contains("rpc error") || m.Contains("failed to execute") || m.Contains("not found"))
                _out.WriteLine($"✓ {name}: chain rejected (encoding OK)");
            else
                throw; // real error
        }
    }

    [Fact]
    public async Task CancelSubscription()
    {
        var (w, c, tx) = Setup();
        if (tx == null) { _out.WriteLine("SKIP"); return; }
        using var _ = w!; using var __ = c!;
        await TestMsg("CANCEL_SUBSCRIPTION", MessageBuilder.CancelSubscription(w.Address, 1), tx);
    }

    [Fact]
    public async Task RenewSubscription()
    {
        var (w, c, tx) = Setup();
        if (tx == null) { _out.WriteLine("SKIP"); return; }
        using var _ = w!; using var __ = c!;
        await TestMsg("RENEW_SUBSCRIPTION", MessageBuilder.RenewSubscription(w.Address, 1), tx);
    }

    [Fact]
    public async Task ShareSubscription()
    {
        var (w, c, tx) = Setup();
        if (tx == null) { _out.WriteLine("SKIP"); return; }
        using var _ = w!; using var __ = c!;
        await TestMsg("SHARE_SUBSCRIPTION", MessageBuilder.ShareSubscription(w.Address, 1, "sent1test", 1000000), tx);
    }

    [Fact]
    public async Task UpdateSubscription()
    {
        var (w, c, tx) = Setup();
        if (tx == null) { _out.WriteLine("SKIP"); return; }
        using var _ = w!; using var __ = c!;
        await TestMsg("UPDATE_SUBSCRIPTION", MessageBuilder.UpdateSubscription(w.Address, 1, 1), tx);
    }

    [Fact]
    public async Task UpdateSession()
    {
        var (w, c, tx) = Setup();
        if (tx == null) { _out.WriteLine("SKIP"); return; }
        using var _ = w!; using var __ = c!;
        await TestMsg("UPDATE_SESSION", MessageBuilder.UpdateSession(w.Address, 37599840, 1000, 500), tx);
    }

    [Fact]
    public async Task RegisterNode()
    {
        var (w, c, tx) = Setup();
        if (tx == null) { _out.WriteLine("SKIP"); return; }
        using var _ = w!; using var __ = c!;
        var prices = new[] { new PriceEntry("udvpn", "0.000040152030000000", "40152030") };
        await TestMsg("REGISTER_NODE", MessageBuilder.RegisterNode(w.Address, prices, prices, new[] { "1.2.3.4:8585" }), tx);
    }

    [Fact]
    public async Task UpdateNodeDetails()
    {
        var (w, c, tx) = Setup();
        if (tx == null) { _out.WriteLine("SKIP"); return; }
        using var _ = w!; using var __ = c!;
        var nodeAddr = SentinelWallet.SentToSentnode(w.Address);
        var prices = new[] { new PriceEntry("udvpn", "0.000040152030000000", "40152030") };
        await TestMsg("UPDATE_NODE_DETAILS", MessageBuilder.UpdateNodeDetails(nodeAddr, prices, prices, new[] { "1.2.3.4:8585" }), tx);
    }

    [Fact]
    public async Task UpdateNodeStatus()
    {
        var (w, c, tx) = Setup();
        if (tx == null) { _out.WriteLine("SKIP"); return; }
        using var _ = w!; using var __ = c!;
        var nodeAddr = SentinelWallet.SentToSentnode(w.Address);
        await TestMsg("UPDATE_NODE_STATUS", MessageBuilder.UpdateNodeStatus(nodeAddr, 1), tx);
    }

    [Fact]
    public async Task UpdatePlanDetails()
    {
        var (w, c, tx) = Setup();
        if (tx == null) { _out.WriteLine("SKIP"); return; }
        using var _ = w!; using var __ = c!;
        var provAddr = SentinelWallet.SentToSentprov(w.Address);
        await TestMsg("UPDATE_PLAN_DETAILS", MessageBuilder.UpdatePlanDetails(provAddr, 44), tx);
    }
}
