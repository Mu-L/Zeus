namespace Zeus;

/// <summary>OPC UA NodeId 标识符形态。点表常用数值与字符串两种。</summary>
public enum OpcUaNodeIdType
{
    /// <summary>无符号整数标识符。</summary>
    Numeric = 0,

    /// <summary>字符串标识符，例如标签名。</summary>
    String = 1,

    /// <summary>GUID 标识符。</summary>
    Guid = 2,

    /// <summary>不透明字节串标识符。</summary>
    Opaque = 3
}
