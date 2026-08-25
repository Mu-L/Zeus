namespace Zeus;

/// <summary>本包会检查或返回的 OPC UA StatusCode。未列出的码仍按 32 位原值抛出。</summary>
public static class OpcUaStatusCodes
{
    /// <summary>成功。</summary>
    public const uint Good = 0x00000000;

    /// <summary>报文无法解码。</summary>
    public const uint BadDecodingError = 0x80070000;

    /// <summary>报文无法编码。</summary>
    public const uint BadEncodingError = 0x80060000;

    /// <summary>请求超时。</summary>
    public const uint BadTimeout = 0x800A0000;

    /// <summary>服务不受支持。</summary>
    public const uint BadServiceUnsupported = 0x800B0000;

    /// <summary>身份令牌无效或策略不匹配。</summary>
    public const uint BadIdentityTokenInvalid = 0x80200000;

    /// <summary>安全通道标识无效。</summary>
    public const uint BadSecureChannelIdInvalid = 0x80220000;

    /// <summary>会话标识无效，通常需要重新 CreateSession。</summary>
    public const uint BadSessionIdInvalid = 0x80250000;

    /// <summary>NodeId 在地址空间中不存在。</summary>
    public const uint BadNodeIdUnknown = 0x80340000;

    /// <summary>节点或属性不可写。</summary>
    public const uint BadNotWritable = 0x803B0000;

    /// <summary>写入值类型与节点声明不一致。</summary>
    public const uint BadTypeMismatch = 0x80740000;

    /// <summary>安全策略不受支持。本包只接受 SecurityPolicy None。</summary>
    public const uint BadSecurityPolicyRejected = 0x80550000;

    /// <summary>判断 StatusCode 是否表示成功（高 2 位为 00）。</summary>
    public static bool IsGood(uint statusCode) => (statusCode & 0xC0000000) == 0;
}
