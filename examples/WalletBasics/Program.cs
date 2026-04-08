using Sentinel.SDK.Core;

// ─── Wallet Basics Example ───
// Generate a new Sentinel wallet, display address, and check balance.
// Demonstrates wallet creation, mnemonic export, and chain queries.
//
// Usage:
//   dotnet run                              # generate new wallet
//   dotnet run -- "word1 word2 ... word24"  # import existing mnemonic

SentinelWallet wallet;

if (args.Length > 0)
{
    Console.WriteLine("Importing wallet from mnemonic...");
    wallet = SentinelWallet.FromMnemonic(args[0]);
}
else
{
    Console.WriteLine("Generating new wallet...");
    wallet = SentinelWallet.Generate();
    var mnemonic = wallet.ExportMnemonicString();
    Console.WriteLine($"\n  Mnemonic (SAVE THIS):\n  {mnemonic}\n");
}

Console.WriteLine($"  Address: {wallet.Address}");

// ── Check balance ──
Console.WriteLine("\nQuerying balance...");
using var chain = new ChainClient();
var balance = await chain.GetBalanceAsync(wallet.Address);
Console.WriteLine($"  Balance: {balance.Display} ({balance.Udvpn} udvpn)");

if (balance.Udvpn == 0)
{
    Console.WriteLine("\n  Wallet is empty. Send P2P tokens to the address above to get started.");
    Console.WriteLine("  You can purchase P2P on supported exchanges or receive from another wallet.");
}

wallet.Dispose();
return 0;
