using System.Globalization;

namespace Sentinel.SDK.Core;

// All protobuf wire-format primitives are in ProtobufWriter.
using static ProtobufWriter;

/// <summary>Record representing a chain message ready for broadcast.</summary>
/// <param name="TypeUrl">Fully-qualified protobuf type URL (e.g. /sentinel.node.v3.MsgStartSessionRequest).</param>
/// <param name="Value">Protobuf wire-format encoded message bytes.</param>
public record SentinelMessage(string TypeUrl, byte[] Value);

/// <summary>
/// Builders for all 19 Sentinel chain message types.
/// Each method encodes protobuf fields using manual wire-format encoding
/// and returns a <see cref="SentinelMessage"/> ready for
/// <see cref="TransactionBuilder.BroadcastAsync"/>.
/// </summary>
public static partial class MessageBuilder
{
    // ─── Protobuf Sub-Message Encoders ───────────────────────────────

    /// <summary>
    /// Encode a <see cref="PriceEntry"/> as sentinel.types.v1.Price.
    /// Fields: denom(1:string), base_value(2:string), quote_value(3:string).
    /// CRITICAL: base_value is sdk.Dec on chain — must be scaled by 10^18 before encoding.
    /// The chain stores Dec values as big integers with 18 decimal places of precision.
    /// Example: "0.003000000000000000" → "3000000000000000"
    /// Example: "40152030" → "40152030000000000000000000"
    /// </summary>
    internal static byte[] EncodePrice(PriceEntry price)
    {
        using var s = new MemoryStream();

        WriteStringField(s, 1, price.Denom);
        WriteStringField(s, 2, DecToScaledInt(price.BaseValue));
        WriteStringField(s, 3, price.QuoteValue);

        return s.ToArray();
    }

    /// <summary>
    /// Convert an sdk.Dec string to its scaled big integer representation (multiply by 10^18).
    /// The Cosmos SDK stores Dec values as integers with 18 decimal places of precision.
    /// This matches the JS SDK's decToScaledInt() function exactly.
    /// </summary>
    /// <param name="decStr">Decimal string from the chain (e.g. "0.003000000000000000" or "40152030").</param>
    /// <returns>Scaled integer string (e.g. "3000000000000000" or "40152030000000000000000000").</returns>
    public static string DecToScaledInt(string decStr)
    {
        if (string.IsNullOrWhiteSpace(decStr))
            return "0";

        var s = decStr.Trim();
        if (s == "undefined" || s == "null" || s.Length == 0)
            return "0";

        var dotIdx = s.IndexOf('.');
        if (dotIdx == -1)
        {
            // Integer — multiply by 10^18
            return s + new string('0', 18);
        }

        var intPart = s[..dotIdx];
        var fracPart = s[(dotIdx + 1)..];

        // Pad or trim fractional part to exactly 18 digits
        var frac18 = fracPart.Length >= 18
            ? fracPart[..18]
            : fracPart + new string('0', 18 - fracPart.Length);

        var combined = (string.IsNullOrEmpty(intPart) || intPart == "0")
            ? frac18
            : intPart + frac18;

        // Remove leading zeros (but keep at least one digit)
        var trimmed = combined.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    /// <summary>
    /// Encode a google.protobuf.Duration.
    /// Fields: seconds(1:int64), nanos(2:int32).
    /// </summary>
    internal static byte[] EncodeDuration(long seconds, int nanos = 0)
    {
        using var s = new MemoryStream();

        if (seconds != 0)
        {
            WriteVarintField(s, 1, (ulong)seconds);
        }

        if (nanos != 0)
        {
            WriteVarintField(s, 2, (ulong)nanos);
        }

        return s.ToArray();
    }

    /// <summary>
    /// Encode a cosmos.base.v1beta1.Coin.
    /// Fields: denom(1:string), amount(2:string).
    /// </summary>
    internal static byte[] EncodeCoin(string denom, string amount)
    {
        using var s = new MemoryStream();

        WriteStringField(s, 1, denom);
        WriteStringField(s, 2, amount);

        return s.ToArray();
    }

    /// <summary>
    /// Encode a google.protobuf.Any.
    /// Fields: type_url(1:string), value(2:bytes).
    /// </summary>
    internal static byte[] EncodeAny(string typeUrl, byte[] value)
    {
        using var s = new MemoryStream();

        WriteStringField(s, 1, typeUrl);
        WriteBytesField(s, 2, value);

        return s.ToArray();
    }

    /// <summary>
    /// Encode a cosmos.feegrant.v1beta1.BasicAllowance.
    /// Fields: spend_limit(1:repeated Coin), expiration(2:Timestamp).
    /// </summary>
    internal static byte[] EncodeBasicAllowance(long? spendLimitUdvpn, DateTime? expiration)
    {
        using var s = new MemoryStream();

        // field 1: spend_limit (repeated Coin — single entry if provided)
        if (spendLimitUdvpn.HasValue)
        {
            var coinBytes = EncodeCoin(Constants.Denom, spendLimitUdvpn.Value.ToString(CultureInfo.InvariantCulture));
            WriteEmbeddedField(s, 1, coinBytes);
        }

        // field 2: expiration (google.protobuf.Timestamp)
        if (expiration.HasValue)
        {
            var timestampBytes = EncodeTimestamp(expiration.Value);
            WriteEmbeddedField(s, 2, timestampBytes);
        }

        return s.ToArray();
    }

    /// <summary>
    /// Encode a google.protobuf.Timestamp.
    /// Fields: seconds(1:int64), nanos(2:int32).
    /// Converts UTC DateTime to Unix epoch seconds.
    /// </summary>
    internal static byte[] EncodeTimestamp(DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var seconds = (long)(utc - epoch).TotalSeconds;

        using var s = new MemoryStream();

        if (seconds != 0)
        {
            WriteVarintField(s, 1, (ulong)seconds);
        }

        return s.ToArray();
    }

    /// <summary>
    /// Encode a cosmos.authz.v1beta1.GenericAuthorization.
    /// Fields: msg(1:string).
    /// </summary>
    internal static byte[] EncodeGenericAuthorization(string msgTypeUrl)
    {
        using var s = new MemoryStream();
        WriteStringField(s, 1, msgTypeUrl);
        return s.ToArray();
    }

    /// <summary>
    /// Encode a cosmos.authz.v1beta1.Grant.
    /// Fields: authorization(1:Any wrapping GenericAuthorization), expiration(2:Timestamp).
    /// </summary>
    internal static byte[] EncodeGrant(string msgTypeUrl, DateTime? expiration)
    {
        using var s = new MemoryStream();

        // field 1: authorization (Any wrapping GenericAuthorization)
        var authBytes = EncodeGenericAuthorization(msgTypeUrl);
        var anyBytes = EncodeAny(
            "/cosmos.authz.v1beta1.GenericAuthorization",
            authBytes);
        WriteEmbeddedField(s, 1, anyBytes);

        // field 2: expiration (google.protobuf.Timestamp, optional)
        if (expiration.HasValue)
        {
            var timestampBytes = EncodeTimestamp(expiration.Value);
            WriteEmbeddedField(s, 2, timestampBytes);
        }

        return s.ToArray();
    }
}
