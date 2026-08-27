using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Zeus;

/// <summary>
/// OPC UA 二进制编码原语。只覆盖本包会话与 Value 读写需要的内置类型。
/// </summary>
internal static class OpcUaBinary
{
    /// <summary>Windows FILETIME 起点，对应 OPC UA DateTime 的 0。</summary>
    private static readonly DateTime FileTimeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static void WriteBoolean(List<byte> buffer, bool value) => buffer.Add(value ? (byte)1 : (byte)0);

    public static void WriteByte(List<byte> buffer, byte value) => buffer.Add(value);

    public static void WriteUInt16(List<byte> buffer, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        Append(buffer, bytes);
    }

    public static void WriteInt32(List<byte> buffer, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        Append(buffer, bytes);
    }

    public static void WriteUInt32(List<byte> buffer, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Append(buffer, bytes);
    }

    public static void WriteInt64(List<byte> buffer, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        Append(buffer, bytes);
    }

    public static void WriteUInt64(List<byte> buffer, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Append(buffer, bytes);
    }

    public static void WriteFloat(List<byte> buffer, float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        Append(buffer, bytes);
    }

    public static void WriteDouble(List<byte> buffer, double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        Append(buffer, bytes);
    }

    public static void WriteString(List<byte> buffer, string? value)
    {
        if (value is null)
        {
            WriteInt32(buffer, -1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(buffer, bytes.Length);
        buffer.AddRange(bytes);
    }

    public static void WriteByteString(List<byte> buffer, byte[]? value)
    {
        if (value is null)
        {
            WriteInt32(buffer, -1);
            return;
        }

        WriteInt32(buffer, value.Length);
        buffer.AddRange(value);
    }

    public static void WriteGuid(List<byte> buffer, Guid value)
    {
        var bytes = value.ToByteArray();
        buffer.AddRange(bytes);
    }

    public static void WriteDateTime(List<byte> buffer, DateTime value)
    {
        if (value == DateTime.MinValue)
        {
            WriteInt64(buffer, 0);
            return;
        }

        var utc = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        WriteInt64(buffer, utc.ToFileTimeUtc());
    }

    public static void WriteNodeId(List<byte> buffer, OpcUaNodeId? nodeId)
    {
        if (nodeId is null)
        {
            buffer.Add(0x00);
            buffer.Add(0x00);
            return;
        }

        switch (nodeId.IdentifierType)
        {
            case OpcUaNodeIdType.Numeric:
                var numeric = Convert.ToUInt32(nodeId.Identifier, CultureInfo.InvariantCulture);
                if (nodeId.NamespaceIndex == 0 && numeric <= byte.MaxValue)
                {
                    buffer.Add(0x00);
                    buffer.Add((byte)numeric);
                    return;
                }

                if (nodeId.NamespaceIndex <= byte.MaxValue && numeric <= ushort.MaxValue)
                {
                    buffer.Add(0x01);
                    buffer.Add((byte)nodeId.NamespaceIndex);
                    WriteUInt16(buffer, (ushort)numeric);
                    return;
                }

                buffer.Add(0x02);
                WriteUInt16(buffer, nodeId.NamespaceIndex);
                WriteUInt32(buffer, numeric);
                return;
            case OpcUaNodeIdType.String:
                buffer.Add(0x03);
                WriteUInt16(buffer, nodeId.NamespaceIndex);
                WriteString(buffer, (string)nodeId.Identifier);
                return;
            case OpcUaNodeIdType.Guid:
                buffer.Add(0x04);
                WriteUInt16(buffer, nodeId.NamespaceIndex);
                WriteGuid(buffer, (Guid)nodeId.Identifier);
                return;
            case OpcUaNodeIdType.Opaque:
                buffer.Add(0x05);
                WriteUInt16(buffer, nodeId.NamespaceIndex);
                WriteByteString(buffer, (byte[])nodeId.Identifier);
                return;
            default:
                throw new OpcUaException($"不支持的 NodeId 类型 {nodeId.IdentifierType}。");
        }
    }

    public static void WriteQualifiedName(List<byte> buffer, ushort namespaceIndex, string? name)
    {
        WriteUInt16(buffer, namespaceIndex);
        WriteString(buffer, name);
    }

    public static void WriteLocalizedText(List<byte> buffer, string? text)
    {
        if (text is null)
        {
            buffer.Add(0x00);
            return;
        }

        buffer.Add(0x02);
        WriteString(buffer, text);
    }

    public static void WriteExtensionObject(List<byte> buffer, OpcUaNodeId? typeId, byte[]? body)
    {
        WriteNodeId(buffer, typeId);
        if (body is null)
        {
            buffer.Add(0x00);
            return;
        }

        buffer.Add(0x01);
        WriteByteString(buffer, body);
    }

    public static void WriteNullExtensionObject(List<byte> buffer)
        => WriteExtensionObject(buffer, null, null);

    public static void WriteDiagnosticInfo(List<byte> buffer)
        => buffer.Add(0x00);

    public static void WriteSignatureData(List<byte> buffer)
    {
        WriteString(buffer, null);
        WriteByteString(buffer, null);
    }

    public static void WriteVariant(List<byte> buffer, OpcUaValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        buffer.Add((byte)value.DataType);
        switch (value.DataType)
        {
            case OpcUaDataType.Boolean:
                WriteBoolean(buffer, Convert.ToBoolean(value.Value, CultureInfo.InvariantCulture));
                break;
            case OpcUaDataType.SByte:
                buffer.Add(unchecked((byte)Convert.ToSByte(value.Value, CultureInfo.InvariantCulture)));
                break;
            case OpcUaDataType.Byte:
                buffer.Add(Convert.ToByte(value.Value, CultureInfo.InvariantCulture));
                break;
            case OpcUaDataType.Int16:
                WriteUInt16(buffer, unchecked((ushort)Convert.ToInt16(value.Value, CultureInfo.InvariantCulture)));
                break;
            case OpcUaDataType.UInt16:
                WriteUInt16(buffer, Convert.ToUInt16(value.Value, CultureInfo.InvariantCulture));
                break;
            case OpcUaDataType.Int32:
                WriteInt32(buffer, Convert.ToInt32(value.Value, CultureInfo.InvariantCulture));
                break;
            case OpcUaDataType.UInt32:
                WriteUInt32(buffer, Convert.ToUInt32(value.Value, CultureInfo.InvariantCulture));
                break;
            case OpcUaDataType.Int64:
                WriteInt64(buffer, Convert.ToInt64(value.Value, CultureInfo.InvariantCulture));
                break;
            case OpcUaDataType.UInt64:
                WriteUInt64(buffer, Convert.ToUInt64(value.Value, CultureInfo.InvariantCulture));
                break;
            case OpcUaDataType.Float:
                WriteFloat(buffer, Convert.ToSingle(value.Value, CultureInfo.InvariantCulture));
                break;
            case OpcUaDataType.Double:
                WriteDouble(buffer, Convert.ToDouble(value.Value, CultureInfo.InvariantCulture));
                break;
            case OpcUaDataType.String:
                WriteString(buffer, Convert.ToString(value.Value, CultureInfo.InvariantCulture));
                break;
            case OpcUaDataType.DateTime:
                WriteDateTime(buffer, value.Value is DateTime time ? time : DateTime.Parse(Convert.ToString(value.Value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
                break;
            case OpcUaDataType.ByteString:
                WriteByteString(buffer, value.Value as byte[] ?? Encoding.UTF8.GetBytes(Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty));
                break;
            default:
                throw new OpcUaException($"不支持的 Variant 类型 {value.DataType}。");
        }
    }

    public static void WriteDataValue(List<byte> buffer, OpcUaValue value)
    {
        byte mask = 0x01;
        if (value.StatusCode != OpcUaStatusCodes.Good)
        {
            mask |= 0x02;
        }

        if (value.SourceTimestamp is not null)
        {
            mask |= 0x04;
        }

        buffer.Add(mask);
        WriteVariant(buffer, value);
        if ((mask & 0x02) != 0)
        {
            WriteUInt32(buffer, value.StatusCode);
        }

        if ((mask & 0x04) != 0)
        {
            WriteDateTime(buffer, value.SourceTimestamp!.Value);
        }
    }

    public static void WriteArray<T>(List<byte> buffer, IReadOnlyList<T> items, Action<List<byte>, T> writeItem)
    {
        WriteInt32(buffer, items.Count);
        foreach (var item in items)
        {
            writeItem(buffer, item);
        }
    }

    public static bool ReadBoolean(ref Reader reader) => reader.ReadByte() != 0;

    public static ushort ReadUInt16(ref Reader reader) => BinaryPrimitives.ReadUInt16LittleEndian(reader.Read(2));

    public static int ReadInt32(ref Reader reader) => BinaryPrimitives.ReadInt32LittleEndian(reader.Read(4));

    public static uint ReadUInt32(ref Reader reader) => BinaryPrimitives.ReadUInt32LittleEndian(reader.Read(4));

    public static long ReadInt64(ref Reader reader) => BinaryPrimitives.ReadInt64LittleEndian(reader.Read(8));

    public static ulong ReadUInt64(ref Reader reader) => BinaryPrimitives.ReadUInt64LittleEndian(reader.Read(8));

    public static float ReadFloat(ref Reader reader) => BinaryPrimitives.ReadSingleLittleEndian(reader.Read(4));

    public static double ReadDouble(ref Reader reader) => BinaryPrimitives.ReadDoubleLittleEndian(reader.Read(8));

    public static string? ReadString(ref Reader reader)
    {
        var length = ReadInt32(ref reader);
        if (length < 0)
        {
            return null;
        }

        return Encoding.UTF8.GetString(reader.Read(length));
    }

    public static byte[]? ReadByteString(ref Reader reader)
    {
        var length = ReadInt32(ref reader);
        if (length < 0)
        {
            return null;
        }

        return reader.Read(length).ToArray();
    }

    public static Guid ReadGuid(ref Reader reader) => new(reader.Read(16));

    public static DateTime ReadDateTime(ref Reader reader)
    {
        var ticks = ReadInt64(ref reader);
        if (ticks <= 0)
        {
            return DateTime.MinValue;
        }

        try
        {
            return DateTime.FromFileTimeUtc(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return FileTimeEpoch.AddTicks(ticks);
        }
    }

    public static OpcUaNodeId ReadNodeId(ref Reader reader)
    {
        var encoding = reader.ReadByte();
        switch (encoding)
        {
            case 0x00:
                return OpcUaNodeId.Numeric(reader.ReadByte());
            case 0x01:
                {
                    var namespaceIndex = reader.ReadByte();
                    return OpcUaNodeId.Numeric(ReadUInt16(ref reader), namespaceIndex);
                }
            case 0x02:
                {
                    var namespaceIndex = ReadUInt16(ref reader);
                    return OpcUaNodeId.Numeric(ReadUInt32(ref reader), namespaceIndex);
                }
            case 0x03:
                {
                    var namespaceIndex = ReadUInt16(ref reader);
                    return OpcUaNodeId.String(ReadString(ref reader) ?? string.Empty, namespaceIndex);
                }
            case 0x04:
                {
                    var namespaceIndex = ReadUInt16(ref reader);
                    return new OpcUaNodeId(namespaceIndex, OpcUaNodeIdType.Guid, ReadGuid(ref reader));
                }
            case 0x05:
                {
                    var namespaceIndex = ReadUInt16(ref reader);
                    return new OpcUaNodeId(namespaceIndex, OpcUaNodeIdType.Opaque, ReadByteString(ref reader) ?? []);
                }
            default:
                throw new OpcUaException($"不支持的 NodeId 编码 0x{encoding:X2}。");
        }
    }

    public static void SkipQualifiedName(ref Reader reader)
    {
        _ = ReadUInt16(ref reader);
        _ = ReadString(ref reader);
    }

    public static void SkipLocalizedText(ref Reader reader)
    {
        var mask = reader.ReadByte();
        if ((mask & 0x01) != 0)
        {
            _ = ReadString(ref reader);
        }

        if ((mask & 0x02) != 0)
        {
            _ = ReadString(ref reader);
        }
    }

    public static void SkipExtensionObject(ref Reader reader)
    {
        _ = ReadNodeId(ref reader);
        var encoding = reader.ReadByte();
        if (encoding != 0)
        {
            _ = ReadByteString(ref reader);
        }
    }

    public static void SkipDiagnosticInfo(ref Reader reader)
    {
        var mask = reader.ReadByte();
        if ((mask & 0x01) != 0)
        {
            _ = ReadInt32(ref reader);
        }

        if ((mask & 0x02) != 0)
        {
            _ = ReadInt32(ref reader);
        }

        if ((mask & 0x04) != 0)
        {
            _ = ReadInt32(ref reader);
        }

        if ((mask & 0x08) != 0)
        {
            _ = ReadInt32(ref reader);
        }

        if ((mask & 0x10) != 0)
        {
            _ = ReadString(ref reader);
        }

        if ((mask & 0x20) != 0)
        {
            _ = ReadUInt32(ref reader);
        }

        if ((mask & 0x40) != 0)
        {
            SkipDiagnosticInfo(ref reader);
        }
    }

    public static void SkipSignatureData(ref Reader reader)
    {
        _ = ReadString(ref reader);
        _ = ReadByteString(ref reader);
    }

    public static OpcUaValue ReadVariant(ref Reader reader)
    {
        var mask = reader.ReadByte();
        if ((mask & 0x80) != 0)
        {
            throw new OpcUaException("当前不支持 OPC UA 数组 Variant。");
        }

        var dataType = (OpcUaDataType)(mask & 0x3F);
        object value = dataType switch
        {
            OpcUaDataType.Boolean => ReadBoolean(ref reader),
            OpcUaDataType.SByte => unchecked((sbyte)reader.ReadByte()),
            OpcUaDataType.Byte => reader.ReadByte(),
            OpcUaDataType.Int16 => unchecked((short)ReadUInt16(ref reader)),
            OpcUaDataType.UInt16 => ReadUInt16(ref reader),
            OpcUaDataType.Int32 => ReadInt32(ref reader),
            OpcUaDataType.UInt32 => ReadUInt32(ref reader),
            OpcUaDataType.Int64 => ReadInt64(ref reader),
            OpcUaDataType.UInt64 => ReadUInt64(ref reader),
            OpcUaDataType.Float => ReadFloat(ref reader),
            OpcUaDataType.Double => ReadDouble(ref reader),
            OpcUaDataType.String => ReadString(ref reader) ?? string.Empty,
            OpcUaDataType.DateTime => ReadDateTime(ref reader),
            OpcUaDataType.ByteString => ReadByteString(ref reader) ?? [],
            _ => throw new OpcUaException($"不支持的 Variant 类型 {dataType}。")
        };
        return new OpcUaValue(dataType, value);
    }

    public static OpcUaValue ReadDataValue(ref Reader reader)
    {
        var mask = reader.ReadByte();
        OpcUaValue? value = null;
        var status = OpcUaStatusCodes.Good;
        DateTime? sourceTimestamp = null;
        if ((mask & 0x01) != 0)
        {
            value = ReadVariant(ref reader);
        }

        if ((mask & 0x02) != 0)
        {
            status = ReadUInt32(ref reader);
        }

        if ((mask & 0x04) != 0)
        {
            sourceTimestamp = ReadDateTime(ref reader);
        }

        if ((mask & 0x08) != 0)
        {
            _ = ReadDateTime(ref reader);
        }

        if ((mask & 0x10) != 0)
        {
            _ = ReadUInt16(ref reader);
        }

        if ((mask & 0x20) != 0)
        {
            _ = ReadUInt16(ref reader);
        }

        value ??= new OpcUaValue(OpcUaDataType.String, null, status, sourceTimestamp);
        return value with { StatusCode = status, SourceTimestamp = sourceTimestamp };
    }

    public static int ReadArrayLength(ref Reader reader)
    {
        var count = ReadInt32(ref reader);
        return count < 0 ? 0 : count;
    }

    private static void Append(List<byte> buffer, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            buffer.Add(value);
        }
    }

    /// <summary>从剩余缓冲顺序读取 OPC UA 二进制字段。</summary>
    internal ref struct Reader
    {
        private ReadOnlySpan<byte> _span;

        public Reader(ReadOnlySpan<byte> span) => _span = span;

        public int Remaining => _span.Length;

        public byte ReadByte()
        {
            if (_span.IsEmpty)
            {
                throw new OpcUaException("OPC UA 报文过短。");
            }

            var value = _span[0];
            _span = _span[1..];
            return value;
        }

        public ReadOnlySpan<byte> Read(int count)
        {
            if (count < 0 || _span.Length < count)
            {
                throw new OpcUaException("OPC UA 报文过短。");
            }

            var slice = _span[..count];
            _span = _span[count..];
            return slice;
        }
    }
}
