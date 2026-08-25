namespace Zeus;

/// <summary>
/// 自动重连调度状态。
/// </summary>
public enum ReconnectState
{
    /// <summary>已安排下一次重连。</summary>
    Scheduled = 0,

    /// <summary>本轮重连成功。</summary>
    Succeeded = 1,

    /// <summary>本轮重连失败，仍可继续退避。</summary>
    Failed = 2,

    /// <summary>已达到最大尝试次数，不再自动重连。</summary>
    GaveUp = 3,

    /// <summary>进入熔断等待窗口。</summary>
    CircuitOpen = 4,

    /// <summary>宿主停止、通道移除或状态恢复导致重连取消。</summary>
    Cancelled = 5
}

