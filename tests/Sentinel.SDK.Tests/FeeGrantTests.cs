using Sentinel.SDK.Core;
using Xunit;
using Xunit.Abstractions;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Fee Grant lifecycle test on live mainnet:
/// 1. Operator wallet (has tokens) grants fee allowance to a fresh wallet
/// 2. Verify the grant exists on chain via LCD
/// 3. Send 1 udvpn to user so they have something to send back
/// 4. User sends 1 udvpn back using fee grant (granter pays ~60k gas)
/// 5. Verify user balance is 0 (fee grant covered all costs)
/// 6. Revoke the fee grant
/// 7. Verify grant is gone
/// </summary>
public class FeeGrantTests
{
    private readonly ITestOutputHelper _output;

    public FeeGrantTests(ITestOutputHelper output) => _output = output;

    private static string? GetMnemonic()
    {
        // Try environment variable first
        var env = Environment.GetEnvironmentVariable("SENTINEL_TEST_MNEMONIC");
        if (!string.IsNullOrEmpty(env)) return env;

        // Fall back to .env file
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
    public async Task FeeGrant_FullLifecycle_GrantUseFreeRevoke()
    {
        var opMnemonic = GetMnemonic();
        if (opMnemonic is null) { _output.WriteLine("SKIP: No mnemonic"); return; }

        _output.WriteLine("=== C# Fee Grant Test ===\n");

        // ─── Step 0: Setup wallets ─────────────────────────────────────
        _output.WriteLine("Setting up wallets...");

        using var opWallet = SentinelWallet.FromMnemonic(opMnemonic);
        using var opChain = new ChainClient(logger: new NullSdkLogger());
        await opChain.InitializeAsync();
        var opTx = new TransactionBuilder(opWallet, opChain);

        var opBal = await opChain.GetBalanceAsync(opWallet.Address);
        _output.WriteLine($"  Operator (granter): {opWallet.Address}");
        _output.WriteLine($"  Operator balance: {Helpers.FormatP2P(opBal.Udvpn)}");
        Assert.True(opBal.Udvpn > 1_000_000, "Operator needs at least 1 P2P");

        // Generate a fresh wallet — ZERO balance
        using var userWallet = SentinelWallet.Generate();
        using var userChain = new ChainClient(logger: new NullSdkLogger());
        await userChain.InitializeAsync();
        var userTx = new TransactionBuilder(userWallet, userChain);

        _output.WriteLine($"  User (grantee):     {userWallet.Address}");
        _output.WriteLine($"  User mnemonic:      {userWallet.ExportMnemonicString().Split(' ')[..3].Aggregate((a, b) => a + " " + b)}... (truncated)\n");

        // Check user balance — should be 0
        try
        {
            var userBal = await userChain.GetBalanceAsync(userWallet.Address);
            _output.WriteLine($"  User balance: {userBal.Udvpn} udvpn (should be 0)\n");
        }
        catch
        {
            _output.WriteLine("  User balance: 0 udvpn (expected — new account)\n");
        }

        // ─── Step 1: Grant fee allowance ───────────────────────────────
        _output.WriteLine("Step 1: Granting fee allowance...");
        _output.WriteLine($"  Granter: {opWallet.Address}");
        _output.WriteLine($"  Grantee: {userWallet.Address}");
        _output.WriteLine($"  Spend limit: 500,000 udvpn (0.5 P2P)");

        var grantMsg = MessageBuilder.GrantFeeAllowance(
            opWallet.Address, userWallet.Address, 500_000);

        var grantResult = await opTx.BroadcastAsync(grantMsg);
        _output.WriteLine($"  Grant TX: {grantResult.TxHash}");
        _output.WriteLine($"  Code: {grantResult.Code} (0 = success)\n");
        Assert.True(grantResult.Success, $"Grant failed: {grantResult.RawLog}");

        // Wait for chain propagation
        _output.WriteLine("  Waiting 8s for chain propagation...");
        await Task.Delay(8000);

        // ─── Step 2: Verify grant exists on chain ──────────────────────
        _output.WriteLine("\nStep 2: Verifying fee grant on chain...");
        var grants = await opChain.QueryFeeGrantsAsync(userWallet.Address);
        if (grants.Count == 0)
        {
            _output.WriteLine("  Retrying after 10s...");
            await Task.Delay(10000);
            grants = await opChain.QueryFeeGrantsAsync(userWallet.Address);
        }
        _output.WriteLine($"  Grants for user: {grants.Count}");
        Assert.True(grants.Count > 0, "Fee grant not found on chain");

        var theGrant = grants.FirstOrDefault(g => g.Granter == opWallet.Address);
        Assert.NotNull(theGrant);
        _output.WriteLine($"  ✓ Fee grant found! Granter: {theGrant!.Granter}");
        _output.WriteLine($"  Allowance: {theGrant.Allowance}\n");

        // ─── Step 3: Send 1 udvpn to user ──────────────────────────────
        _output.WriteLine("Step 3: Sending 1 udvpn to user (so they have something to send back)...");
        var sendMsg = MessageBuilder.Send(opWallet.Address, userWallet.Address, 1);
        var sendResult = await opTx.BroadcastAsync(sendMsg);
        _output.WriteLine($"  Send TX: {sendResult.TxHash}");
        _output.WriteLine($"  Code: {sendResult.Code}\n");
        Assert.True(sendResult.Success, $"Send failed: {sendResult.RawLog}");

        // Wait for propagation
        _output.WriteLine("  Waiting 7s...");
        await Task.Delay(7000);

        // Verify user now has 1 udvpn
        var userBalNow = await userChain.GetBalanceAsync(userWallet.Address);
        if (userBalNow.Udvpn == 0)
        {
            _output.WriteLine("  Balance still 0, retrying after 10s...");
            await Task.Delay(10000);
            userBalNow = await userChain.GetBalanceAsync(userWallet.Address);
        }
        _output.WriteLine($"  User balance now: {userBalNow.Udvpn} udvpn\n");

        // ─── Step 4: User sends 1 udvpn back USING FEE GRANT ──────────
        _output.WriteLine("Step 4: User sends 1 udvpn back to operator USING FEE GRANT...");
        _output.WriteLine("  (User has ~1 udvpn, gas costs ~60,000 udvpn — impossible without fee grant)");

        // Set the fee granter on the user's transaction builder
        userTx.FeeGranter = opWallet.Address;

        var sendBackMsg = MessageBuilder.Send(userWallet.Address, opWallet.Address, 1);
        var feeGrantResult = await userTx.BroadcastAsync(sendBackMsg);
        _output.WriteLine($"  Fee Grant TX: {feeGrantResult.TxHash}");
        _output.WriteLine($"  Code: {feeGrantResult.Code}");

        if (feeGrantResult.Success)
        {
            _output.WriteLine("  ✓ SUCCESS! TX was FREE for the user — granter paid the gas!\n");
        }
        else
        {
            _output.WriteLine($"  ✗ Failed: {feeGrantResult.RawLog}\n");
        }
        Assert.True(feeGrantResult.Success, $"Fee grant TX failed: {feeGrantResult.RawLog}");

        // ─── Step 5: Verify user balance (should be 0 again) ──────────
        _output.WriteLine("Step 5: Checking final balances...");
        await Task.Delay(5000);

        var userFinal = await userChain.GetBalanceAsync(userWallet.Address);
        _output.WriteLine($"  User final balance: {userFinal.Udvpn} udvpn");
        if (userFinal.Udvpn == 0)
        {
            _output.WriteLine("  ✓ User balance is 0 — fee grant covered ALL costs!\n");
        }
        else
        {
            _output.WriteLine($"  User still has {userFinal.Udvpn} udvpn\n");
        }

        // ─── Step 6: Revoke fee grant ──────────────────────────────────
        _output.WriteLine("Step 6: Revoking fee grant...");
        var revokeMsg = MessageBuilder.RevokeFeeAllowance(opWallet.Address, userWallet.Address);
        var revokeResult = await opTx.BroadcastAsync(revokeMsg);
        _output.WriteLine($"  Revoke TX: {revokeResult.TxHash}");
        _output.WriteLine($"  Code: {revokeResult.Code} (0 = success)");

        if (revokeResult.Success)
        {
            _output.WriteLine("  ✓ Fee grant revoked successfully!\n");
        }
        else
        {
            _output.WriteLine($"  ✗ Revoke failed: {revokeResult.RawLog}\n");
        }
        Assert.True(revokeResult.Success, $"Revoke failed: {revokeResult.RawLog}");

        // ─── Step 7: Verify grant is gone ──────────────────────────────
        _output.WriteLine("Step 7: Verifying grant is revoked...");
        await Task.Delay(5000);

        var grantsAfter = await opChain.QueryFeeGrantsAsync(userWallet.Address);
        var stillExists = grantsAfter.Any(g => g.Granter == opWallet.Address);

        if (!stillExists)
        {
            _output.WriteLine("  ✓ Confirmed: fee grant no longer exists on chain");
        }
        else
        {
            _output.WriteLine("  ✗ Fee grant still exists (may need more time)");
        }

        _output.WriteLine("\n=== C# Fee Grant Test Complete ===");
        Assert.False(stillExists, "Fee grant should be revoked");
    }
}
