namespace Zeus;

/// <summary>
/// 报警严重等级。用于列表排序、颜色和通知策略，与高低报方向 <see cref="PointAlarmState"/> 正交。
/// </summary>
public enum PointAlarmSeverity
{
    /// <summary>提示，不要求立即处理。</summary>
    Info = 0,

    /// <summary>警告，需要关注。</summary>
    Warning = 1,

    /// <summary>报警，需要操作员处理。</summary>
    Alarm = 2,

    /// <summary>严重，需要立即处理。</summary>
    Critical = 3
}
