using System.Text;

namespace Sentinel.SDK.Core;

/// <summary>Shared protobuf wire-format encoding primitives.</summary>
internal static class ProtobufWriter
{
    // ─── Low-Level Primitives ───────────────────────────────────────

    /// <summary>Write a protobuf field tag (field_number &lt;&lt; 3 | wire_type).</summary>
    public static void WriteTag(Stream stream, int fieldNumber, int wireType)
    {
        WriteVarint(stream, (ulong)((fieldNumber << 3) | wireType));
    }

    /// <summary>Write a variable-length integer (base-128 varint).</summary>
    public static void WriteVarint(Stream stream, ulong value)
    {
        while (value > 0x7F)
        {
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    /// <summary>Write a length-delimited byte array.</summary>
    public static void WriteBytes(Stream stream, byte[] data)
    {
        WriteVarint(stream, (ulong)data.Length);
        stream.Write(data, 0, data.Length);
    }

    /// <summary>Write a length-delimited UTF-8 string.</summary>
    public static void WriteString(Stream stream, string value)
    {
        WriteBytes(stream, Encoding.UTF8.GetBytes(value));
    }

    // ─── Convenience Field Writers ──────────────────────────────────

    /// <summary>Write a varint field (tag + varint value).</summary>
    public static void WriteVarintField(Stream stream, int field, ulong value)
    {
        WriteTag(stream, field, 0);
        WriteVarint(stream, value);
    }

    /// <summary>Write a string field (tag + length-delimited string).</summary>
    public static void WriteStringField(Stream stream, int field, string value)
    {
        WriteTag(stream, field, 2);
        WriteString(stream, value);
    }

    /// <summary>Write a bytes field (tag + length-delimited bytes).</summary>
    public static void WriteBytesField(Stream stream, int field, byte[] value)
    {
        WriteTag(stream, field, 2);
        WriteBytes(stream, value);
    }

    /// <summary>Write an embedded message field (tag + length-delimited encoded message).</summary>
    public static void WriteEmbeddedField(Stream stream, int field, byte[] value)
    {
        WriteTag(stream, field, 2);
        WriteBytes(stream, value);
    }
}
