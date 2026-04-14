using System.Text;

namespace Sentinel.SDK.Core;

/// <summary>
/// Minimal protobuf wire-format decoder for parsing RPC ABCI query responses.
/// Complements ProtobufWriter (encoder) to give the SDK read + write capability.
/// </summary>
internal static class ProtobufReader
{
    /// <summary>Decoded protobuf field.</summary>
    internal record ProtoField(int FieldNumber, int WireType, ReadOnlyMemory<byte> Data, ulong Varint);

    /// <summary>
    /// Decode a protobuf message into a list of fields.
    /// Wire type 0 = varint, 2 = length-delimited, 1 = 64-bit, 5 = 32-bit.
    /// </summary>
    internal static List<ProtoField> Decode(ReadOnlySpan<byte> buf)
    {
        var fields = new List<ProtoField>();
        var i = 0;

        while (i < buf.Length)
        {
            var tag = ReadVarint(buf, ref i);
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);

            switch (wireType)
            {
                case 0: // varint
                    var val = ReadVarint(buf, ref i);
                    fields.Add(new ProtoField(fieldNumber, wireType, ReadOnlyMemory<byte>.Empty, val));
                    break;

                case 2: // length-delimited
                    var len = (int)ReadVarint(buf, ref i);
                    var data = buf.Slice(i, len).ToArray();
                    i += len;
                    fields.Add(new ProtoField(fieldNumber, wireType, data, 0));
                    break;

                case 1: // 64-bit fixed
                    i += 8;
                    break;

                case 5: // 32-bit fixed
                    i += 4;
                    break;

                default:
                    return fields; // unknown wire type — stop parsing
            }
        }

        return fields;
    }

    /// <summary>Read a base-128 varint from the buffer.</summary>
    internal static ulong ReadVarint(ReadOnlySpan<byte> buf, ref int offset)
    {
        ulong result = 0;
        var shift = 0;
        while (offset < buf.Length)
        {
            var b = buf[offset++];
            result |= (ulong)(b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0) break;
        }
        return result;
    }

    /// <summary>Get all fields with the given field number.</summary>
    internal static List<ProtoField> GetFields(List<ProtoField> fields, int fieldNumber) =>
        fields.Where(f => f.FieldNumber == fieldNumber).ToList();

    /// <summary>Get the first field with the given number, or null.</summary>
    internal static ProtoField? GetField(List<ProtoField> fields, int fieldNumber) =>
        fields.FirstOrDefault(f => f.FieldNumber == fieldNumber);

    /// <summary>Decode a length-delimited field as UTF-8 string.</summary>
    internal static string DecodeString(ProtoField field) =>
        Encoding.UTF8.GetString(field.Data.Span);

    /// <summary>Decode an embedded message (length-delimited) into sub-fields.</summary>
    internal static List<ProtoField> DecodeEmbedded(ProtoField field) =>
        Decode(field.Data.Span);

    /// <summary>Decode a PriceEntry from protobuf fields (denom=1, base_value=2, quote_value=3).</summary>
    internal static PriceEntry DecodePrice(List<ProtoField> fields)
    {
        var denom = GetField(fields, 1) is { } d ? DecodeString(d) : "";
        var baseVal = GetField(fields, 2) is { } b ? DecodeString(b) : "0";
        var quoteVal = GetField(fields, 3) is { } q ? DecodeString(q) : "0";
        return new PriceEntry(denom, baseVal, quoteVal);
    }

    /// <summary>
    /// Decode a ChainSession from protobuf fields.
    /// Session v3 wraps in base_session (field 1). Base session proto:
    /// id=1, acc_address=2, node_address=3, download_bytes=7(varint), upload_bytes=8(varint),
    /// max_bytes=9(varint), duration=10(string), max_duration=11(string), status=14(varint),
    /// inactive_at=15(embedded timestamp), start_at=16(embedded timestamp)
    /// </summary>
    internal static ChainSession DecodeSession(List<ProtoField> outerFields)
    {
        // Unwrap base_session (field 1) if present
        var baseField = GetField(outerFields, 1);
        var fields = baseField is not null ? DecodeEmbedded(baseField) : outerFields;

        var id = GetField(fields, 1) is { } f1 ? f1.Varint.ToString() : "0";
        var accAddr = GetField(fields, 2) is { } f2 ? DecodeString(f2) : "";
        var nodeAddr = GetField(fields, 3) is { } f3 ? DecodeString(f3) : "";
        var download = GetField(fields, 7) is { } f7 ? f7.Varint.ToString() : "0";
        var upload = GetField(fields, 8) is { } f8 ? f8.Varint.ToString() : "0";
        var maxBytes = GetField(fields, 9) is { } f9 ? f9.Varint.ToString() : "0";
        var duration = GetField(fields, 10) is { } f10 ? DecodeString(f10) : null;
        var maxDuration = GetField(fields, 11) is { } f11 ? DecodeString(f11) : null;
        var status = GetField(fields, 14) is { } f14 ? (int)f14.Varint : 0;
        // status: 1=active, 2=inactive_pending, 3=inactive
        var statusStr = status switch { 1 => "active", 2 => "inactive_pending", 3 => "inactive", _ => status.ToString() };

        return new ChainSession(id, accAddr, nodeAddr, download, upload, maxBytes, duration, maxDuration, statusStr, null, null);
    }

    /// <summary>
    /// Decode a Subscription from protobuf fields.
    /// Subscription v3 wraps in base_subscription (field 1). Base:
    /// id=1(varint), acc_address=2(string), plan_id=4(varint), status=7(varint),
    /// start_at=8(timestamp), inactive_at=9(timestamp)
    /// Price is on outer field 2.
    /// </summary>
    internal static Subscription DecodeSubscription(List<ProtoField> outerFields)
    {
        var baseField = GetField(outerFields, 1);
        var fields = baseField is not null ? DecodeEmbedded(baseField) : outerFields;

        var id = GetField(fields, 1) is { } f1 ? f1.Varint.ToString() : "0";
        var accAddr = GetField(fields, 2) is { } f2 ? DecodeString(f2) : "";
        var planId = GetField(fields, 4) is { } f4 ? f4.Varint.ToString() : "0";
        var status = GetField(fields, 7) is { } f7 ? (int)f7.Varint : 0;
        var statusStr = status switch { 1 => "active", 2 => "inactive_pending", 3 => "inactive", _ => status.ToString() };

        // Price is outer field 2 (on the subscription wrapper, not base)
        PriceEntry? price = null;
        if (GetField(outerFields, 2) is { } pf)
        {
            price = DecodePrice(DecodeEmbedded(pf));
        }

        return new Subscription(id, accAddr, planId, price, statusStr, "", "");
    }

    /// <summary>
    /// Decode a Provider from protobuf fields.
    /// Provider v2: address=1, name=2, identity=3, website=4, description=5, status=6(varint)
    /// </summary>
    internal static Provider DecodeProvider(List<ProtoField> fields)
    {
        var address = GetField(fields, 1) is { } f1 ? DecodeString(f1) : "";
        var name = GetField(fields, 2) is { } f2 ? DecodeString(f2) : "";
        var identity = GetField(fields, 3) is { } f3 ? DecodeString(f3) : "";
        var website = GetField(fields, 4) is { } f4 ? DecodeString(f4) : "";
        var description = GetField(fields, 5) is { } f5 ? DecodeString(f5) : "";
        var status = GetField(fields, 6) is { } f6 ? (int)f6.Varint : 0;
        return new Provider(address, name, identity, website, description, status);
    }

    /// <summary>
    /// Decode a ChainNode from protobuf fields.
    /// Node proto: address=1, gigabyte_prices=2, hourly_prices=3, remote_addrs=4, status=6
    /// </summary>
    internal static ChainNode DecodeNode(List<ProtoField> fields)
    {
        var address = GetField(fields, 1) is { } a ? DecodeString(a) : "";
        var gbPrices = GetFields(fields, 2)
            .Select(f => DecodePrice(DecodeEmbedded(f)))
            .ToArray();
        var hrPrices = GetFields(fields, 3)
            .Select(f => DecodePrice(DecodeEmbedded(f)))
            .ToArray();
        var remoteAddrs = GetFields(fields, 4)
            .Select(DecodeString)
            .ToArray();
        var status = GetField(fields, 6) is { } s ? (int)s.Varint : 0;

        // Derive RemoteUrl from first remote_addr
        var remoteUrl = remoteAddrs.Length > 0
            ? (remoteAddrs[0].StartsWith("http") ? remoteAddrs[0] : $"https://{remoteAddrs[0]}")
            : null;

        return new ChainNode(address, remoteAddrs, remoteUrl, gbPrices, hrPrices, status);
    }
}
