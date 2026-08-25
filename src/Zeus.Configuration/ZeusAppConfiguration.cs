using System.Text.Json;

namespace Zeus;

/// <summary>
/// Zeus 工程配置根对象，对应一份 JSON 文件。
/// 通道、设备与采集均可在监视开启时热更新；拓扑变更会增删运行中的实例。
/// </summary>
public sealed class ZeusAppConfiguration
{
    /// <summary>周期采集。</summary>
    public AcquisitionConfiguration Acquisition { get; set; } = new();

    /// <summary>通道故障后的自动重连。省略则使用框架默认（开启、1 秒起、上限 30 秒）。</summary>
    public ReconnectConfiguration Reconnect { get; set; } = new();

    /// <summary>传输通道列表。</summary>
    public List<ChannelConfiguration> Channels { get; set; } = [];

    /// <summary>设备列表。引用的通道必须先出现在 <see cref="Channels"/> 中。</summary>
    public List<DeviceConfiguration> Devices { get; set; } = [];

}

/// <summary>
/// 通道故障自动重连的 JSON 配置。
/// </summary>
public sealed class ReconnectConfiguration
{
    /// <summary>是否启用自动重连，默认 true。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>首次重连等待（毫秒），默认 1000。</summary>
    public int InitialDelayMilliseconds { get; set; } = 1000;

    /// <summary>退避上限（毫秒），默认 30000。</summary>
    public int MaxDelayMilliseconds { get; set; } = 30000;

    /// <summary>连续失败时的等待倍数，默认 2。</summary>
    public double BackoffMultiplier { get; set; } = 2;

    /// <summary>最大连续重连次数。0 表示不限制。</summary>
    public int MaxAttempts { get; set; }

    /// <summary>退避抖动比例，0 表示关闭。</summary>
    public double JitterRatio { get; set; }

    /// <summary>达到最大尝试次数后的熔断等待（毫秒）。0 表示停止自动重连。</summary>
    public int CircuitBreakMilliseconds { get; set; }
}

/// <summary>
/// 采集循环配置。
/// </summary>
public sealed class AcquisitionConfiguration
{
    /// <summary>两轮间隔（毫秒），默认 500。</summary>
    public int IntervalMilliseconds { get; set; } = 500;

    /// <summary>启动后是否立刻采第一轮，默认 true。</summary>
    public bool PollImmediately { get; set; } = true;

    /// <summary>
    /// 单个采集源本轮超时（毫秒）。省略或 0 表示不在宿主层额外限时。
    /// </summary>
    public int SourceTimeoutMilliseconds { get; set; }

    /// <summary>
    /// 同一通道上的设备是否串行轮询，默认 true。
    /// </summary>
    public bool SerializePerChannel { get; set; } = true;
}

/// <summary>
/// 一条通道的配置。
/// </summary>
public sealed class ChannelConfiguration
{
    /// <summary>通道名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 类型：<c>virtual</c>、<c>serial</c>、<c>tcp</c>、<c>tcp-server</c>、<c>udp</c>、<c>udp-server</c>。
    /// </summary>
    public string Type { get; set; } = "virtual";

    /// <summary>启动策略：required、optional 或 degraded。</summary>
    public string Startup { get; set; } = "required";

    /// <summary>通道类型的专属选项，例如串口、网络端点或虚拟从站参数。</summary>
    public Dictionary<string, JsonElement> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 一台设备的配置。
/// </summary>
public sealed class DeviceConfiguration
{
    /// <summary>设备名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>绑定的通道名。</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>类型：<c>modbus-rtu</c>、<c>modbus-tcp</c>、<c>modbus-ascii</c>、<c>mitsubishi-mc</c>、<c>siemens-s7</c>、<c>omron-fins</c>、<c>omron-host-link</c>、<c>panasonic-mewtocol</c>、<c>ethernet-ip</c>、<c>dlt645</c>、<c>iec104</c>、<c>mqtt</c> 或 <c>snmp</c>。</summary>
    public string Type { get; set; } = "modbus-rtu";

    /// <summary>协议专属选项，例如站号、超时、帧格式或会话参数。</summary>
    public Dictionary<string, JsonElement> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>周期采集点。</summary>
    public List<PointConfiguration> Points { get; set; } = [];
}

/// <summary>
/// 一个采集点的配置。
/// </summary>
public sealed class PointConfiguration
{
    /// <summary>点名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>点位选项，例如地址、数据类型、缩放、写权限与报警阈值。</summary>
    public Dictionary<string, JsonElement> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
