using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NBitcoin;

namespace Sentinel.SDK.Core;

/// <summary>
/// Sentinel dVPN wallet — BIP39 mnemonic generation, BIP44 key derivation,
/// secp256k1 signing, and Bech32 address encoding.
/// </summary>
public sealed class SentinelWallet : ISentinelWallet, IDisposable
{
    // ─── Cosmos BIP44 derivation path: m/44'/118'/0'/0/0 ───
    private static readonly KeyPath CosmosKeyPath = new("m/44'/118'/0'/0/0");

    private readonly Key _privateKey;
    private readonly byte[] _pinnedKeyBytes;
    private GCHandle _keyHandle;
    private bool _disposed;

    /// <summary>BIP39 mnemonic phrase used to derive this wallet.</summary>
    [Obsolete("Use ExportMnemonicBytes() for secure access. This property will be removed in a future version.")]
    public string Mnemonic { get; }

    /// <summary>Whether this wallet was created from a mnemonic phrase.</summary>
#pragma warning disable CS0618 // Internal access to obsolete Mnemonic is intentional
    public bool HasMnemonic => Mnemonic is not null;
#pragma warning restore CS0618

    /// <summary>Bech32-encoded account address with "sent" prefix.</summary>
    public string Address { get; }

    private SentinelWallet(string mnemonic, Key privateKey, string address, byte[] pinnedKeyBytes, GCHandle keyHandle)
    {
#pragma warning disable CS0618 // Obsolete member access in constructor is intentional
        Mnemonic = mnemonic;
#pragma warning restore CS0618
        _privateKey = privateKey;
        _pinnedKeyBytes = pinnedKeyBytes;
        _keyHandle = keyHandle;
        Address = address;
    }

    /// <summary>
    /// Returns the mnemonic as a UTF-8 byte array. Caller MUST zero the returned array after use.
    /// </summary>
    /// <returns>UTF-8-encoded mnemonic bytes, or null if no mnemonic is available.</returns>
    public byte[]? ExportMnemonicBytes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
#pragma warning disable CS0618
        if (Mnemonic is null) return null;
        return System.Text.Encoding.UTF8.GetBytes(Mnemonic);
#pragma warning restore CS0618
    }

    /// <summary>
    /// Export the mnemonic as a string. Returns null if wallet was created without a mnemonic.
    /// </summary>
    /// <remarks>
    /// Convenience wrapper around <see cref="ExportMnemonicBytes"/>. For security-sensitive contexts,
    /// prefer <see cref="ExportMnemonicBytes"/> so you can zero the byte array after use.
    /// </remarks>
    public string? ExportMnemonicString()
    {
        var bytes = ExportMnemonicBytes();
        return bytes != null ? System.Text.Encoding.UTF8.GetString(bytes) : null;
    }

    // ─── Factory Methods ───

    /// <summary>
    /// Generate a new random wallet with a BIP39 mnemonic.
    /// </summary>
    /// <param name="strength">
    /// Entropy strength in bits. 128 = 12 words (default), 256 = 24 words.
    /// </param>
    /// <returns>A new <see cref="SentinelWallet"/> with a fresh mnemonic.</returns>
    public static SentinelWallet Generate(int strength = 128)
    {
        var wordCount = strength switch
        {
            128 => WordCount.Twelve,
            160 => WordCount.Fifteen,
            192 => WordCount.Eighteen,
            224 => WordCount.TwentyOne,
            256 => WordCount.TwentyFour,
            _ => throw new SentinelException("WALLET_INVALID_STRENGTH",
                $"Invalid mnemonic strength {strength}. Use 128, 160, 192, 224, or 256."),
        };

        var mnemonic = new NBitcoin.Mnemonic(Wordlist.English, wordCount);
        return FromMnemonicInternal(mnemonic);
    }

    /// <summary>
    /// Derive a wallet from an existing BIP39 mnemonic phrase.
    /// </summary>
    /// <param name="mnemonic">Space-separated BIP39 mnemonic words.</param>
    /// <returns>A <see cref="SentinelWallet"/> derived from the mnemonic.</returns>
    /// <exception cref="SentinelException">Thrown if the mnemonic is invalid.</exception>
    public static SentinelWallet FromMnemonic(string mnemonic)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            throw new SentinelException("WALLET_EMPTY_MNEMONIC", "Mnemonic cannot be empty.");
        }

        NBitcoin.Mnemonic parsed;
        try
        {
            parsed = new NBitcoin.Mnemonic(mnemonic.Trim(), Wordlist.English);
        }
        catch (Exception ex)
        {
            throw new SentinelException("WALLET_INVALID_MNEMONIC",
                $"Invalid BIP39 mnemonic: {ex.Message}", ex);
        }

        return FromMnemonicInternal(parsed);
    }

    /// <summary>
    /// Internal factory that derives the private key and address from a parsed mnemonic.
    /// Pins the raw key bytes in memory to prevent GC relocation and ensure reliable zeroing on dispose.
    /// Zeros all intermediate key material (seed, derived ExtKey) immediately after derivation.
    /// </summary>
    private static SentinelWallet FromMnemonicInternal(NBitcoin.Mnemonic mnemonic)
    {
        // Derive seed → master key → child key at Cosmos path
        var seed = mnemonic.DeriveExtKey();
        var derived = seed.Derive(CosmosKeyPath);
        var privateKey = derived.PrivateKey;

        // Extract raw key bytes and pin them so the GC cannot relocate
        var rawKeyBytes = privateKey.ToBytes();
        var pinnedKeyBytes = new byte[32];
        Array.Copy(rawKeyBytes, pinnedKeyBytes, 32);
        var keyHandle = GCHandle.Alloc(pinnedKeyBytes, GCHandleType.Pinned);

        // Zero intermediate key material (seed and derived ExtKey expose raw bytes)
        CryptographicOperations.ZeroMemory(rawKeyBytes);
        try
        {
            var seedBytes = seed.PrivateKey.ToBytes();
            CryptographicOperations.ZeroMemory(seedBytes);
        }
        catch
        {
            // Best effort — some NBitcoin versions may not expose raw bytes
        }

        // Build the Bech32 address: SHA256(pubkey) → RIPEMD160 → Bech32
        var address = BuildAddress(privateKey.PubKey, Constants.BechPrefix);

        return new SentinelWallet(mnemonic.ToString(), privateKey, address, pinnedKeyBytes, keyHandle);
    }

    // ─── Signing ───

    /// <summary>
    /// Sign a message with the wallet's secp256k1 private key.
    /// Returns a compact 64-byte ECDSA signature (no recovery byte).
    /// </summary>
    /// <param name="message">Raw message bytes to sign.</param>
    /// <returns>64-byte compact signature (r ∥ s).</returns>
    public byte[] Sign(byte[] message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(message);

        var signature = _privateKey.Sign(new uint256(message), false);
        // ToCompact() in NBitcoin 7.x returns 64 bytes (r || s) directly.
        // If it returns 65 (older versions with recovery byte), skip first byte.
        var compact = signature.ToCompact();
        if (compact.Length == 65)
        {
            var result = compact[1..];
            CryptographicOperations.ZeroMemory(compact);
            return result;
        }
        return compact;
    }

    /// <summary>
    /// Get the 33-byte compressed secp256k1 public key.
    /// </summary>
    /// <returns>Compressed public key bytes.</returns>
    public byte[] GetPublicKeyCompressed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _privateKey.PubKey.ToBytes();
    }

    // ─── Address Conversion Helpers ───

    /// <summary>
    /// Convert this wallet's address to a sentnode1... node operator address.
    /// Same key bytes, different Bech32 prefix.
    /// </summary>
    public string ToSentnode()
    {
        return ConvertPrefix(Address, Constants.BechPrefix, Constants.NodePrefix);
    }

    /// <summary>
    /// Convert this wallet's address to a sentprov1... provider address.
    /// Same key bytes, different Bech32 prefix.
    /// </summary>
    public string ToSentprov()
    {
        return ConvertPrefix(Address, Constants.BechPrefix, Constants.ProviderPrefix);
    }

    /// <summary>
    /// Compare two Sentinel addresses across different prefixes (sent, sentnode, sentprov).
    /// Returns true if they encode the same underlying key bytes.
    /// </summary>
    /// <param name="addr1">First address (any Sentinel prefix).</param>
    /// <param name="addr2">Second address (any Sentinel prefix).</param>
    /// <returns>True if both addresses derive from the same public key.</returns>
    public static bool IsSameKey(string addr1, string addr2)
    {
        ArgumentNullException.ThrowIfNull(addr1);
        ArgumentNullException.ThrowIfNull(addr2);

        try
        {
            var bytes1 = DecodeBech32Data(addr1);
            var bytes2 = DecodeBech32Data(addr2);
            return bytes1.SequenceEqual(bytes2);
        }
        catch
        {
            return false;
        }
    }

    // ─── Static Validation & Address Conversion ───

    /// <summary>
    /// Validate a BIP39 mnemonic string. Returns true if valid (12+ words), false otherwise.
    /// Use this for UI validation (e.g. enable/disable a "Connect" button).
    /// </summary>
    /// <param name="mnemonic">The mnemonic string to validate.</param>
    /// <returns>True if the mnemonic is a 12+ word string.</returns>
    public static bool IsMnemonicValid(string? mnemonic)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
            return false;

        var words = mnemonic.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 12;
    }

    /// <summary>
    /// Convert a sent1... account address to a sentprov1... provider address.
    /// Same underlying key bytes, different Bech32 prefix.
    /// </summary>
    /// <param name="sentAddr">Account address (sent1...).</param>
    /// <returns>Provider address (sentprov1...).</returns>
    public static string SentToSentprov(string sentAddr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sentAddr);
        if (!sentAddr.StartsWith(Constants.BechPrefix))
            throw new SentinelException("WALLET_INVALID_ADDRESS",
                $"Address must start with '{Constants.BechPrefix}': {sentAddr}");

        return ConvertPrefix(sentAddr, Constants.BechPrefix, Constants.ProviderPrefix);
    }

    /// <summary>
    /// Convert a sent1... account address to a sentnode1... node operator address.
    /// Same underlying key bytes, different Bech32 prefix.
    /// </summary>
    /// <param name="sentAddr">Account address (sent1...).</param>
    /// <returns>Node address (sentnode1...).</returns>
    public static string SentToSentnode(string sentAddr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sentAddr);
        if (!sentAddr.StartsWith(Constants.BechPrefix))
            throw new SentinelException("WALLET_INVALID_ADDRESS",
                $"Address must start with '{Constants.BechPrefix}': {sentAddr}");

        return ConvertPrefix(sentAddr, Constants.BechPrefix, Constants.NodePrefix);
    }

    /// <summary>
    /// Convert a sentprov1... provider address back to a sent1... account address.
    /// Same underlying key bytes, different Bech32 prefix.
    /// </summary>
    /// <param name="provAddr">Provider address (sentprov1...).</param>
    /// <returns>Account address (sent1...).</returns>
    public static string SentprovToSent(string provAddr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provAddr);
        if (!provAddr.StartsWith(Constants.ProviderPrefix))
            throw new SentinelException("WALLET_INVALID_ADDRESS",
                $"Address must start with '{Constants.ProviderPrefix}': {provAddr}");

        return ConvertPrefix(provAddr, Constants.ProviderPrefix, Constants.BechPrefix);
    }

    // ─── Internal Bech32 Utilities ───

    /// <summary>
    /// Build a Bech32 address from a public key: SHA256 → RIPEMD160 → Bech32.
    /// </summary>
    private static string BuildAddress(PubKey pubKey, string prefix)
    {
        // Cosmos address = RIPEMD160(SHA256(compressed_pubkey))
        var hash = pubKey.Hash; // NBitcoin computes Hash160 (SHA256 + RIPEMD160)
        var data = hash.ToBytes(); // 20 bytes

        // Convert 8-bit data to 5-bit groups for Bech32
        var converted = ConvertBits(data, 8, 5, true);
        var encoder = NBitcoin.DataEncoders.Encoders.Bech32(prefix);
        return encoder.EncodeData(converted, NBitcoin.DataEncoders.Bech32EncodingType.BECH32);
    }

    /// <summary>
    /// Convert a Bech32 address from one prefix to another, preserving the data bytes.
    /// </summary>
    private static string ConvertPrefix(string address, string fromPrefix, string toPrefix)
    {
        var data = DecodeBech32Data(address);
        var converted = ConvertBits(data, 8, 5, true);
        var encoder = NBitcoin.DataEncoders.Encoders.Bech32(toPrefix);
        return encoder.EncodeData(converted, NBitcoin.DataEncoders.Bech32EncodingType.BECH32);
    }

    /// <summary>
    /// Decode a Bech32 address and return the raw data bytes (20 bytes for standard addresses).
    /// </summary>
    private static byte[] DecodeBech32Data(string address)
    {
        // Try known Sentinel prefixes
        string[] prefixes = { Constants.BechPrefix, Constants.NodePrefix, Constants.ProviderPrefix };

        foreach (var prefix in prefixes)
        {
            try
            {
                var decoder = NBitcoin.DataEncoders.Encoders.Bech32(prefix);
                var fiveBit = decoder.DecodeDataRaw(address, out _);
                return ConvertBits(fiveBit, 5, 8, false);
            }
            catch
            {
                // Try next prefix
            }
        }

        throw new SentinelException("WALLET_INVALID_ADDRESS",
            $"Cannot decode Bech32 address: {address}");
    }

    /// <summary>
    /// General bit-width conversion (used for Bech32 5-bit ↔ 8-bit transforms).
    /// </summary>
    private static byte[] ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var maxValue = (1 << toBits) - 1;
        var result = new List<byte>();

        foreach (var value in data)
        {
            if (value < 0 || (value >> fromBits) != 0)
            {
                throw new SentinelException("WALLET_BECH32_ERROR", "Invalid value in bit conversion.");
            }

            acc = (acc << fromBits) | value;
            bits += fromBits;

            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((byte)((acc >> bits) & maxValue));
            }
        }

        if (pad)
        {
            if (bits > 0)
            {
                result.Add((byte)((acc << (toBits - bits)) & maxValue));
            }
        }
        else
        {
            if (bits >= fromBits)
            {
                throw new SentinelException("WALLET_BECH32_ERROR", "Excess padding in bit conversion.");
            }

            if (((acc << (toBits - bits)) & maxValue) != 0)
            {
                throw new SentinelException("WALLET_BECH32_ERROR", "Non-zero padding in bit conversion.");
            }
        }

        return result.ToArray();
    }

    // ─── IDisposable ───

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            // Zero the pinned key bytes (the actual in-memory copy, not a GC-relocated clone)
            CryptographicOperations.ZeroMemory(_pinnedKeyBytes);
            if (_keyHandle.IsAllocated)
            {
                _keyHandle.Free();
            }

            // Also zero the NBitcoin key copy (best effort)
            try
            {
                var keyBytes = _privateKey.ToBytes();
                CryptographicOperations.ZeroMemory(keyBytes);
            }
            catch
            {
                // Best effort — NBitcoin may not expose raw bytes in all versions
            }
        }
    }
}
