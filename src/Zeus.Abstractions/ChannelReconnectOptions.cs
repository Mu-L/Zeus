namespace Zeus;

/// <summary>
/// 通道故障后的自动重连选项。
/// 仅对进入 <see cref="ChannelState.Faulted"/> 的通道生效；主动关闭不会重连。
/// </summary>
public sealed class ChannelReconnectOptions
{
    /// <summary>
    /// 是否在通道故障后自动再次 <see cref="IChannel.OpenAsync"/>。
    /// 默认开启；现场若要自行控制重连，设为 <c>false</c>。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>首次重连等待时间，默认 1 秒。</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>指数退避的上限，默认 30 秒。</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 连续失败时把等待时间乘以该系数。必须大于或等于 1，默认 2。
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2;

    /// <summary>
    /// 最大连续重连次数。0 表示不限制。
    /// </summary>
    public int MaxAttempts { get; set; }

    /// <summary>
    /// 延迟抖动比例，0 表示关闭。0.2 表示在基础延迟上下浮动 20%。
    /// </summary>
    public double JitterRatio { get; set; }

    /// <summary>
    /// 达到最大尝试次数后的熔断等待时间。0 表示直接停止自动重连。
    /// </summary>
    public TimeSpan CircuitBreakDuration { get; set; }

    /// <summary>自动重连调度状态变化。</summary>
    public event EventHandler<ReconnectStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 发布重连状态。由宿主重连服务调用。
    /// </summary>
    public void PublishState(ReconnectStateChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        StateChanged?.Invoke(this, args);
    }
}
