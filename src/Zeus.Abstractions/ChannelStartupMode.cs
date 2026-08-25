namespace Zeus;

/// <summary>
/// 通道启动失败时宿主应采取的策略。
/// </summary>
public enum ChannelStartupMode
{
    /// <summary>通道必须打开成功；失败会让宿主启动失败。</summary>
    Required = 0,

    /// <summary>通道允许暂时不可用；宿主继续启动，自动重连仍可接管。</summary>
    Optional = 1,

    /// <summary>通道失败会进入降级运行；宿主继续启动，并明确记录降级状态。</summary>
    DegradedAllowed = 2
}

