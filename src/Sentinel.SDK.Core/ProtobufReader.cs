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
