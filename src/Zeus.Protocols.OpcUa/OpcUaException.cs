namespace Zeus;

/// <summary>OPC UA 服务或编解码失败。含规范 StatusCode，便于对照手册。</summary>
public sealed class OpcUaException : ZeusProtocolException
{
    /// <summary>创建带状态码的 OPC UA 异常。</summary>
    /// <param name="statusCode">规范 StatusCode，例如 <c>0x80340000</c> 表示未知 NodeId。</param>
    /// <param name="message">面向现场的说明。</param>
    public OpcUaException(uint statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>创建仅含说明的 OPC UA 异常，状态码按编码错误处理。</summary>
    public OpcUaException(string message)
        : this(OpcUaStatusCodes.BadDecodingError, message)
    {
    }

    /// <summary>OPC UA StatusCode。</summary>
    public uint StatusCode { get; }
}
