using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;

namespace Sentinel.SDK.Core;

// All protobuf wire-format primitives are in ProtobufWriter.
using static ProtobufWriter;

/// <summary>
/// Builds, signs, and broadcasts Cosmos SDK transactions on the Sentinel chain.
/// Implements SIGN_MODE_DIRECT with secp256k1 signing.
/// </summary>
public sealed class TransactionBuilder
{
    private readonly SentinelWallet _wallet;
    private readonly ChainClient _client;

    /// <summary>
    /// Fee granter address. When set, the granter pays gas fees instead of the signer.
    /// Set this to the plan provider's address to use their fee grant.
    /// </summary>
    public string? FeeGranter { get; set; }

    /// <summary>
    /// Cached account sequence to avoid querying the chain on every broadcast.
    /// Reset on sequence mismatch (code 32).
    /// </summary>
    private ulong? _cachedSequence;
    private ulong? _cachedAccountNumber;

    /// <summary>
    /// Gas multiplier for safety margin (1.4x estimated gas).
    /// </summary>
    private const double GAS_MULTIPLIER = 1.4;

    /// <summary>
    /// Default gas limit when estimation is not available.
    /// </summary>
    private const ulong DEFAULT_GAS = 200_000;

    /// <summary>
    /// Maximum retry count for sequence mismatch errors.
    /// </summary>
    private const int MAX_SEQUENCE_RETRIES = 6;

    /// <summary>
    /// Create a transaction builder for the given wallet and chain client.
    /// </summary>
    /// <param name="wallet">Wallet to sign transactions with.</param>
    /// <param name="client">Chain client for account queries and broadcasting.</param>
    public TransactionBuilder(SentinelWallet wallet, ChainClient client)
    {
        _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// Build, sign, and broadcast a transaction containing one or more protobuf IMessage objects.
    /// Handles account sequence management with automatic retry on mismatch.
    /// Checks for double-spend before retrying: if the previous TX was already committed,
    /// returns that result instead of retrying.
    /// </summary>
    /// <param name="messages">Protobuf messages to include in the transaction.
    /// Each must be an IMessage with a valid Descriptor (for Any wrapping).</param>
    /// <returns>Transaction result with hash, code, and log.</returns>
    public async Task<TxResult> BroadcastProtobufAsync(params IMessage[] messages)
    {
        if (messages == null || messages.Length == 0)
        {
            throw new SentinelException("TX_NO_MESSAGES", "At least one message is required.");
        }

        string? lastTxHash = null;

        for (var retry = 0; retry < MAX_SEQUENCE_RETRIES; retry++)
        {
            try
            {
                // Step 1: Use cached sequence if available, otherwise query chain
                ulong accountNumber, sequence;
                if (_cachedSequence.HasValue && _cachedAccountNumber.HasValue)
                {
                    accountNumber = _cachedAccountNumber.Value;
                    sequence = _cachedSequence.Value;
                    _cachedSequence = sequence + 1;
                }
                else
                {
                    (accountNumber, sequence) = await _client.GetAccountInfoAsync(_wallet.Address);
                    _cachedAccountNumber = accountNumber;
                    _cachedSequence = sequence + 1;
                }

                // Step 2: Build TxBody with Any-wrapped messages
                var txBodyBytes = BuildTxBody(messages);

                // Step 3: Estimate gas and fee
                var gas = EstimateGas(messages);
                var feeAmount = CalculateFee(gas);

                // Step 4: Build AuthInfo (signer info + fee)
                var authInfoBytes = BuildAuthInfo(sequence, gas, feeAmount, FeeGranter);

                // Step 5: Build SignDoc and sign
                var signDocBytes = BuildSignDoc(txBodyBytes, authInfoBytes, accountNumber);
                var hash = SHA256.HashData(signDocBytes);
                var signature = _wallet.Sign(hash);

                // Step 6: Build TxRaw
                var txRawBytes = BuildTxRaw(txBodyBytes, authInfoBytes, signature);

                // Step 7: Broadcast
                var result = await _client.BroadcastTxAsync(txRawBytes);

                // Check for sequence mismatch (code 32)
                if (result.Code == 32 && retry < MAX_SEQUENCE_RETRIES - 1)
                {
                    // Reset cache on mismatch
                    _cachedSequence = null;
                    _cachedAccountNumber = null;

                    // Before retrying, check if the previous TX was already committed
                    if (lastTxHash != null)
                    {
                        var existingTx = await _client.QueryTxAsync(lastTxHash);
                        if (existingTx != null && existingTx.Code == 0)
                        {
                            return existingTx;
                        }
                    }

                    lastTxHash = result.TxHash;
                    await Task.Delay(Math.Min(2000 * (retry + 1), 6000)); // Backoff: 2s, 4s, 6s, 6s, 6s, 6s
                    continue;
                }

                return result;
            }
            catch (SentinelException ex) when (
                ex.Code == "CLIENT_ALL_ENDPOINTS_FAILED" && retry < MAX_SEQUENCE_RETRIES - 1)
            {
                // Before retrying, check if the previous TX was already committed
                if (lastTxHash != null)
                {
                    var existingTx = await _client.QueryTxAsync(lastTxHash);
                    if (existingTx != null && existingTx.Code == 0)
                    {
                        return existingTx;
                    }
                }

                await Task.Delay(Math.Min(2000 * (retry + 1), 6000));
            }
        }

        throw new SentinelException("TX_MAX_RETRIES",
            "Transaction failed after maximum retry attempts.");
    }

    // ─── Protobuf Wire-Format Encoding ───
    //
    // We manually encode the Cosmos TX envelope using protobuf wire format
    // because importing the full cosmos proto dependency tree is impractical.
    //
    // Wire format reference:
    //   - Field tag = (field_number << 3) | wire_type
    //   - Wire type 0 = varint, 2 = length-delimited (bytes/string/embedded message)
    //

    /// <summary>
    /// Broadcast pre-encoded SentinelMessage objects (from MessageBuilder).
    /// Checks for double-spend before retrying sequence mismatch errors.
    /// </summary>
    public async Task<TxResult> BroadcastAsync(params SentinelMessage[] messages)
    {
        if (messages == null || messages.Length == 0)
            throw new SentinelException("TX_NO_MESSAGES", "At least one message is required.");

        string? lastTxHash = null;

        for (var retry = 0; retry < MAX_SEQUENCE_RETRIES; retry++)
        {
            try
            {
                // Use cached sequence if available, otherwise query chain
                ulong accountNumber, sequence;
                if (_cachedSequence.HasValue && _cachedAccountNumber.HasValue)
                {
                    accountNumber = _cachedAccountNumber.Value;
                    sequence = _cachedSequence.Value;
                    _cachedSequence = sequence + 1; // pre-increment for next call
                }
                else
                {
                    (accountNumber, sequence) = await _client.GetAccountInfoAsync(_wallet.Address);
                    _cachedAccountNumber = accountNumber;
                    _cachedSequence = sequence + 1;
                }

                var txBodyBytes = BuildTxBodyFromRaw(messages);
                var gas = (ulong)(200_000 * messages.Length);
                var feeAmount = CalculateFee(gas);
                var authInfoBytes = BuildAuthInfo(sequence, gas, feeAmount, FeeGranter);
                var signDocBytes = BuildSignDoc(txBodyBytes, authInfoBytes, accountNumber);
                var hash = SHA256.HashData(signDocBytes);
                var signature = _wallet.Sign(hash);
                var txRawBytes = BuildTxRaw(txBodyBytes, authInfoBytes, signature);
                var result = await _client.BroadcastTxAsync(txRawBytes);
                if (result.Code == 32)
                {
                    // Sequence mismatch — reset cache and retry
                    _cachedSequence = null;
                    _cachedAccountNumber = null;

                    // Before retrying, check if the previous TX was already committed
                    if (lastTxHash != null)
                    {
                        var existingTx = await _client.QueryTxAsync(lastTxHash);
                        if (existingTx != null && existingTx.Code == 0)
                        {
                            return existingTx;
                        }
                    }

                    lastTxHash = result.TxHash;
                    await Task.Delay(Math.Min(2000 * (retry + 1), 6000));
                    continue;
                }
                return result;
            }
            catch (SentinelException ex) when (
                ex.Code == "CLIENT_ALL_ENDPOINTS_FAILED" && retry < MAX_SEQUENCE_RETRIES - 1)
            {
                // All endpoints failed — may be transient, retry with backoff
                _cachedSequence = null;
                _cachedAccountNumber = null;

                // Before retrying, check if the previous TX was already committed
                if (lastTxHash != null)
                {
                    var existingTx = await _client.QueryTxAsync(lastTxHash);
                    if (existingTx != null && existingTx.Code == 0)
                    {
                        return existingTx;
                    }
                }

                await Task.Delay(Math.Min(2000 * (retry + 1), 6000));
            }
        }
        throw new SentinelException("TX_SEQUENCE_EXHAUSTED", "Broadcast failed after max retries on sequence mismatch.");
    }

    private static byte[] BuildTxBodyFromRaw(SentinelMessage[] messages)
    {
        using var stream = new MemoryStream();
        foreach (var msg in messages)
        {
            // Build Any: { string type_url = 1; bytes value = 2; }
            using var anyStream = new MemoryStream();
            WriteTag(anyStream, 1, 2);
            WriteString(anyStream, msg.TypeUrl);
            WriteTag(anyStream, 2, 2);
            WriteBytes(anyStream, msg.Value);
            var anyBytes = anyStream.ToArray();

            // TxBody field 1 (messages), wire type 2
            WriteTag(stream, 1, 2);
            WriteBytes(stream, anyBytes);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Build TxBody: contains Any-wrapped messages.
    /// TxBody proto: { repeated Any messages = 1; ... }
    /// </summary>
    private static byte[] BuildTxBody(IMessage[] messages)
    {
        using var stream = new MemoryStream();

        foreach (var msg in messages)
        {
            // Wrap each message as google.protobuf.Any
            var anyBytes = WrapAsAny(msg);

            // Field 1 (messages), wire type 2 (length-delimited)
            WriteTag(stream, 1, 2);
            WriteBytes(stream, anyBytes);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Wrap a protobuf message as google.protobuf.Any.
    /// Any proto: { string type_url = 1; bytes value = 2; }
    /// </summary>
    private static byte[] WrapAsAny(IMessage message)
    {
        using var stream = new MemoryStream();

        // Field 1: type_url (string)
        var typeUrl = $"/{message.Descriptor.FullName}";
        WriteTag(stream, 1, 2);
        WriteString(stream, typeUrl);

        // Field 2: value (serialized message bytes)
        var valueBytes = message.ToByteArray();
        WriteTag(stream, 2, 2);
        WriteBytes(stream, valueBytes);

        return stream.ToArray();
    }

    /// <summary>
    /// Build AuthInfo: signer info + fee.
    /// AuthInfo proto: { repeated SignerInfo signer_infos = 1; Fee fee = 2; }
    /// </summary>
    private byte[] BuildAuthInfo(ulong sequence, ulong gasLimit, ulong feeAmount, string? granter = null)
    {
        using var stream = new MemoryStream();

        // Field 1: signer_infos (SignerInfo)
        var signerInfoBytes = BuildSignerInfo(sequence);
        WriteTag(stream, 1, 2);
        WriteBytes(stream, signerInfoBytes);

        // Field 2: fee (Fee) — includes granter if fee grant exists
        var feeBytes = BuildFee(gasLimit, feeAmount, granter);
        WriteTag(stream, 2, 2);
        WriteBytes(stream, feeBytes);

        return stream.ToArray();
    }

    /// <summary>
    /// Build SignerInfo: public key + mode info + sequence.
    /// SignerInfo proto: { Any public_key = 1; ModeInfo mode_info = 2; uint64 sequence = 3; }
    /// </summary>
    private byte[] BuildSignerInfo(ulong sequence)
    {
        using var stream = new MemoryStream();

        // Field 1: public_key (Any-wrapped secp256k1 PubKey)
        var pubKeyAnyBytes = BuildPubKeyAny();
        WriteTag(stream, 1, 2);
        WriteBytes(stream, pubKeyAnyBytes);

        // Field 2: mode_info (ModeInfo with SIGN_MODE_DIRECT = 1)
        var modeInfoBytes = BuildModeInfo();
        WriteTag(stream, 2, 2);
        WriteBytes(stream, modeInfoBytes);

        // Field 3: sequence (varint)
        WriteTag(stream, 3, 0);
        WriteVarint(stream, sequence);

        return stream.ToArray();
    }

    /// <summary>
    /// Build the public key as Any-wrapped cosmos.crypto.secp256k1.PubKey.
    /// PubKey proto: { bytes key = 1; }
    /// </summary>
    private byte[] BuildPubKeyAny()
    {
        using var stream = new MemoryStream();

        // type_url for secp256k1 public key
        const string typeUrl = "/cosmos.crypto.secp256k1.PubKey";

        // PubKey message: { bytes key = 1; }
        using var pubKeyStream = new MemoryStream();
        WriteTag(pubKeyStream, 1, 2);
        WriteBytes(pubKeyStream, _wallet.GetPublicKeyCompressed());
        var pubKeyBytes = pubKeyStream.ToArray();

        // Any wrapper
        WriteTag(stream, 1, 2);
        WriteString(stream, typeUrl);
        WriteTag(stream, 2, 2);
        WriteBytes(stream, pubKeyBytes);

        return stream.ToArray();
    }

    /// <summary>
    /// Build ModeInfo for SIGN_MODE_DIRECT.
    /// ModeInfo proto: { oneof sum { Single single = 1; } }
    /// Single proto: { SignMode mode = 1; }
    /// SIGN_MODE_DIRECT = 1
    /// </summary>
    private static byte[] BuildModeInfo()
    {
        using var stream = new MemoryStream();

        // Single message: { mode = 1 (SIGN_MODE_DIRECT) }
        using var singleStream = new MemoryStream();
        WriteTag(singleStream, 1, 0); // field 1 = mode, wire type 0 = varint
        WriteVarint(singleStream, 1);  // SIGN_MODE_DIRECT = 1
        var singleBytes = singleStream.ToArray();

        // ModeInfo field 1: single (embedded message)
        WriteTag(stream, 1, 2);
        WriteBytes(stream, singleBytes);

        return stream.ToArray();
    }

    /// <summary>
    /// Build Fee: gas limit + amount.
    /// Fee proto: { repeated Coin amount = 1; uint64 gas_limit = 2; ... }
    /// Coin proto: { string denom = 1; string amount = 2; }
    /// </summary>
    private static byte[] BuildFee(ulong gasLimit, ulong feeAmount, string? granter = null)
    {
        using var stream = new MemoryStream();

        // Field 1: amount (Coin)
        using var coinStream = new MemoryStream();
        WriteTag(coinStream, 1, 2);
        WriteString(coinStream, Constants.Denom);
        WriteTag(coinStream, 2, 2);
        WriteString(coinStream, feeAmount.ToString(CultureInfo.InvariantCulture));
        var coinBytes = coinStream.ToArray();

        WriteTag(stream, 1, 2);
        WriteBytes(stream, coinBytes);

        // Field 2: gas_limit (varint)
        WriteTag(stream, 2, 0);
        WriteVarint(stream, gasLimit);

        // Field 4: granter (string) — fee grant: granter pays gas instead of signer
        if (!string.IsNullOrEmpty(granter))
        {
            WriteTag(stream, 4, 2);
            WriteString(stream, granter);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Build SignDoc: the bytes that get SHA256-hashed and signed.
    /// SignDoc proto: {
    ///   bytes body_bytes = 1;
    ///   bytes auth_info_bytes = 2;
    ///   string chain_id = 3;
    ///   uint64 account_number = 4;
    /// }
    /// </summary>
    private static byte[] BuildSignDoc(byte[] txBodyBytes, byte[] authInfoBytes, ulong accountNumber)
    {
        using var stream = new MemoryStream();

        // Field 1: body_bytes
        WriteTag(stream, 1, 2);
        WriteBytes(stream, txBodyBytes);

        // Field 2: auth_info_bytes
        WriteTag(stream, 2, 2);
        WriteBytes(stream, authInfoBytes);

        // Field 3: chain_id
        WriteTag(stream, 3, 2);
        WriteString(stream, Constants.ChainId);

        // Field 4: account_number
        WriteTag(stream, 4, 0);
        WriteVarint(stream, accountNumber);

        return stream.ToArray();
    }

    /// <summary>
    /// Build TxRaw: the final transaction envelope for broadcasting.
    /// TxRaw proto: {
    ///   bytes body_bytes = 1;
    ///   bytes auth_info_bytes = 2;
    ///   repeated bytes signatures = 3;
    /// }
    /// </summary>
    private static byte[] BuildTxRaw(byte[] txBodyBytes, byte[] authInfoBytes, byte[] signature)
    {
        using var stream = new MemoryStream();

        // Field 1: body_bytes
        WriteTag(stream, 1, 2);
        WriteBytes(stream, txBodyBytes);

        // Field 2: auth_info_bytes
        WriteTag(stream, 2, 2);
        WriteBytes(stream, authInfoBytes);

        // Field 3: signatures
        WriteTag(stream, 3, 2);
        WriteBytes(stream, signature);

        return stream.ToArray();
    }

    // ─── Gas Estimation ───

    /// <summary>
    /// Estimate gas for a set of messages.
    /// Uses a base gas per message type with a safety multiplier.
    /// </summary>
    private static ulong EstimateGas(IMessage[] messages)
    {
        ulong baseGas = 0;

        foreach (var msg in messages)
        {
            var typeName = msg.Descriptor.FullName;

            // Estimate gas based on message type
            baseGas += typeName switch
            {
                var t when t.Contains("MsgSubscribe") => 250_000UL,
                var t when t.Contains("MsgStart") => 200_000UL,
                var t when t.Contains("MsgEnd") => 150_000UL,
                var t when t.Contains("MsgSend") => 100_000UL,
                var t when t.Contains("MsgDelegate") => 200_000UL,
                _ => DEFAULT_GAS,
            };
        }

        return (ulong)(baseGas * GAS_MULTIPLIER);
    }

    /// <summary>
    /// Calculate the fee in udvpn from gas limit.
    /// fee = ceil(gas * gasPrice)
    /// </summary>
    private static ulong CalculateFee(ulong gasLimit)
    {
        var gasPrice = decimal.Parse(Constants.GasPrice, CultureInfo.InvariantCulture);
        return (ulong)Math.Ceiling(gasLimit * gasPrice);
    }

}
