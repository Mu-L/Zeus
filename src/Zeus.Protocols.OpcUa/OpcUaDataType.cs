namespace Zeus;

/// <summary>
/// OPC UA 内置标量类型。只覆盖上位机点表常用的 Value 类型，不包含 ExtensionObject 等复杂结构。
/// </summary>
public enum OpcUaDataType
{
    /// <summary>布尔。</summary>
    Boolean = 1,

    /// <summary>有符号 8 位整数。</summary>
    SByte = 2,

    /// <summary>无符号 8 位整数。</summary>
    Byte = 3,

    /// <summary>有符号 16 位整数。</summary>
    Int16 = 4,

    /// <summary>无符号 16 位整数。</summary>
    UInt16 = 5,

    /// <summary>有符号 32 位整数。</summary>
    Int32 = 6,

    /// <summary>无符号 32 位整数。</summary>
    UInt32 = 7,

    /// <summary>有符号 64 位整数。</summary>
    Int64 = 8,

    /// <summary>无符号 64 位整数。</summary>
    UInt64 = 9,

    /// <summary>单精度浮点。</summary>
    Float = 10,

    /// <summary>双精度浮点。</summary>
    Double = 11,

    /// <summary>UTF-8 文本。</summary>
    String = 12,

    /// <summary>UTC 时间，线上为 Windows FILETIME。</summary>
    DateTime = 13,

    /// <summary>原始字节串。</summary>
    ByteString = 15
}
