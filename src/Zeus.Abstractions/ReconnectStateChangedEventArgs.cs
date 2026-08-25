namespace Zeus;

/// <summary>
/// 自动重连状态变化事件参数。
/// </summary>
public sealed class ReconnectStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// 创建重连状态变化事件。
    /// </summary>
    public ReconnectStateChangedEventArgs(
        string channelName,
        ReconnectState state,
        int attempt,
        TimeSpan? delay = null,
        Exception? error = null)
    {
        ChannelName = string.IsNullOrWhiteSpace(channelName)
            ? throw new ArgumentException("通道名不能为空。", nameof(channelName))
            : channelName.Trim();
        State = state;
        Attempt = attempt;
        Delay = delay;
        Error = error;
    }

    /// <summary>通道名。</summary>
    public string ChannelName { get; }

    /// <summary>当前重连状态。</summary>
    public ReconnectState State { get; }

    /// <summary>当前尝试序号，从 1 开始；取消或复位时可能为 0。</summary>
    public int Attempt { get; }

    /// <summary>下一次动作等待时间。</summary>
    public TimeSpan? Delay { get; }

    /// <summary>导致失败或熔断的异常。</summary>
    public Exception? Error { get; }
}

