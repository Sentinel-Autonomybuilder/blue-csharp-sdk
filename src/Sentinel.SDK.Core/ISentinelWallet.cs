namespace Sentinel.SDK.Core;

/// <summary>
/// Interface for Sentinel wallet operations required by node handshake and session management.
/// </summary>
public interface ISentinelWallet
{
    /// <summary>Bech32-encoded account address with "sent" prefix.</summary>
    string Address { get; }

    /// <summary>
    /// Sign a message hash with the wallet's secp256k1 private key.
    /// Returns a compact 64-byte ECDSA signature (r || s).
    /// </summary>
    /// <param name="hash">32-byte SHA256 hash to sign.</param>
    /// <returns>64-byte compact signature.</returns>
    byte[] Sign(byte[] hash);

    /// <summary>
    /// Get the 33-byte compressed secp256k1 public key.
    /// </summary>
    /// <returns>Compressed public key bytes.</returns>
    byte[] GetPublicKeyCompressed();
}
