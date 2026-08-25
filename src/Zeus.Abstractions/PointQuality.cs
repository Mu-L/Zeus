namespace Zeus;

/// <summary>
/// 点快照的数据质量。值、错误说明与质量一起描述当前点状态。
/// </summary>
public enum PointQuality
{
    /// <summary>尚未采集或质量未知。</summary>
    Unknown = 0,

    /// <summary>最近一次采集或写回成功。</summary>
    Good = 1,

    /// <summary>最近一次采集或写回失败，当前值可能是保留的旧值。</summary>
    Bad = 2,

    /// <summary>值来自旧采样或外部标记的非实时状态。</summary>
    Stale = 3
}
