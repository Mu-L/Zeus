namespace Zeus;

/// <summary>OPC UA 会话选项。当前只覆盖 opc.tcp + SecurityPolicy None 的常用现场。</summary>
public sealed class OpcUaOptions
{
    /// <summary>对端 EndpointUrl，写入 HEL 与 CreateSession。为空时使用 <c>opc.tcp://localhost:4840</c>。</summary>
    public string? EndpointUrl { get; set; }

    /// <summary>应用 URI。为空时由设备名生成。</summary>
    public string? ApplicationUri { get; set; }

    /// <summary>应用显示名。为空时使用设备名。</summary>
    public string? ApplicationName { get; set; }

    /// <summary>会话名。为空时使用设备名。</summary>
    public string? SessionName { get; set; }

    /// <summary>请求的会话超时，单位毫秒。默认 60 秒。</summary>
    public double RequestedSessionTimeout { get; set; } = 60_000;

    /// <summary>匿名登录。为 <c>false</c> 时必须提供用户名。</summary>
    public bool Anonymous { get; set; } = true;

    /// <summary>用户名。仅在 <see cref="Anonymous"/> 为 <c>false</c> 时发送。</summary>
    public string? Username { get; set; }

    /// <summary>密码。只有设置用户名时才会发送。</summary>
    public string? Password { get; set; }

    /// <summary>通道重新打开后是否自动重建安全通道和会话。</summary>
    public bool AutomaticReconnect { get; set; } = true;
}
