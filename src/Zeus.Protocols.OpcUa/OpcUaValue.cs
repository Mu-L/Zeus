using System.Globalization;
using System.Text;

namespace Zeus;

/// <summary>一个 OPC UA DataValue 的工程侧表示。质量码与源时间戳一并保留。</summary>
public sealed record OpcUaValue(OpcUaDataType DataType, object? Value, uint StatusCode = OpcUaStatusCodes.Good, DateTime? SourceTimestamp = null)
{
    /// <summary>创建布尔值。</summary>
    public static OpcUaValue Boolean(bool value) => new(OpcUaDataType.Boolean, value);

    /// <summary>创建有符号 8 位整数。</summary>
    public static OpcUaValue SByte(sbyte value) => new(OpcUaDataType.SByte, value);

    /// <summary>创建无符号 8 位整数。</summary>
    public static OpcUaValue Byte(byte value) => new(OpcUaDataType.Byte, value);

    /// <summary>创建有符号 16 位整数。</summary>
    public static OpcUaValue Int16(short value) => new(OpcUaDataType.Int16, value);

    /// <summary>创建无符号 16 位整数。</summary>
    public static OpcUaValue UInt16(ushort value) => new(OpcUaDataType.UInt16, value);

    /// <summary>创建有符号 32 位整数。</summary>
    public static OpcUaValue Int32(int value) => new(OpcUaDataType.Int32, value);

    /// <summary>创建无符号 32 位整数。</summary>
    public static OpcUaValue UInt32(uint value) => new(OpcUaDataType.UInt32, value);

    /// <summary>创建有符号 64 位整数。</summary>
    public static OpcUaValue Int64(long value) => new(OpcUaDataType.Int64, value);

    /// <summary>创建无符号 64 位整数。</summary>
    public static OpcUaValue UInt64(ulong value) => new(OpcUaDataType.UInt64, value);

    /// <summary>创建单精度浮点。</summary>
    public static OpcUaValue Float(float value) => new(OpcUaDataType.Float, value);

    /// <summary>创建双精度浮点。</summary>
    public static OpcUaValue Double(double value) => new(OpcUaDataType.Double, value);

    /// <summary>创建 UTF-8 文本。</summary>
    public static OpcUaValue String(string value) => new(OpcUaDataType.String, value ?? string.Empty);

    /// <summary>创建 UTC 时间。</summary>
    public static OpcUaValue DateTime(DateTime value) => new(OpcUaDataType.DateTime, value.ToUniversalTime());

    /// <summary>创建原始字节串。</summary>
    public static OpcUaValue ByteString(byte[] value) => new(OpcUaDataType.ByteString, value.ToArray());

    /// <summary>按目标类型把工程值装箱为 OPC UA 值。</summary>
    public static OpcUaValue FromObject(OpcUaDataType dataType, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return dataType switch
        {
            OpcUaDataType.Boolean => Boolean(ConvertBoolean(value)),
            OpcUaDataType.SByte => SByte(Convert.ToSByte(value, CultureInfo.InvariantCulture)),
            OpcUaDataType.Byte => Byte(Convert.ToByte(value, CultureInfo.InvariantCulture)),
            OpcUaDataType.Int16 => Int16(Convert.ToInt16(value, CultureInfo.InvariantCulture)),
            OpcUaDataType.UInt16 => UInt16(Convert.ToUInt16(value, CultureInfo.InvariantCulture)),
            OpcUaDataType.Int32 => Int32(Convert.ToInt32(value, CultureInfo.InvariantCulture)),
            OpcUaDataType.UInt32 => UInt32(Convert.ToUInt32(value, CultureInfo.InvariantCulture)),
            OpcUaDataType.Int64 => Int64(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            OpcUaDataType.UInt64 => UInt64(Convert.ToUInt64(value, CultureInfo.InvariantCulture)),
            OpcUaDataType.Float => Float(Convert.ToSingle(value, CultureInfo.InvariantCulture)),
            OpcUaDataType.Double => Double(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            OpcUaDataType.String => String(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
            OpcUaDataType.DateTime => DateTime(value is DateTime time ? time : System.DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
            OpcUaDataType.ByteString when value is byte[] bytes => ByteString(bytes),
            OpcUaDataType.ByteString => ByteString(Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)),
            _ => throw new OpcUaException($"不支持的 OPC UA 数据类型 {dataType}。")
        };
    }

    private static bool ConvertBoolean(object value)
    {
        if (value is bool bit)
        {
            return bit;
        }

        if (value is string text)
        {
            return text.Trim() switch
            {
                "1" => true,
                "0" => false,
                _ when bool.TryParse(text, out var parsed) => parsed,
                _ => throw new OpcUaException($"OPC UA 布尔值 {text} 无效。")
            };
        }

        return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }
}
