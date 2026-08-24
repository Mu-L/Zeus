namespace Zeus;

/// <summary>
/// 点报警队列。采集越限时产生记录；操作员可确认，点回到正常范围时自动复归。
/// </summary>
public interface IPointAlarmTable
{
    /// <summary>当前未复归的报警，按产生时间从旧到新。</summary>
    IReadOnlyList<PointAlarmRecord> Active { get; }

    /// <summary>最近已复归的报警，按复归时间从旧到新。</summary>
    IReadOnlyList<PointAlarmRecord> History { get; }

    /// <summary>报警产生、确认或复归时触发。可能在采集线程上发出。</summary>
    event EventHandler<PointAlarmChangedEventArgs>? Changed;

    /// <summary>
    /// 按记录标识确认一条活动报警。已确认或已复归时幂等返回当前记录。
    /// </summary>
    /// <param name="id">报警记录标识。</param>
    /// <param name="acknowledgedBy">确认人，可省略。</param>
    PointAlarmRecord Acknowledge(Guid id, string? acknowledgedBy = null);

    /// <summary>
    /// 确认指定点当前未复归的报警。该点没有活动报警时返回 <c>null</c>。
    /// </summary>
    /// <param name="pointName">短名或限定名。</param>
    /// <param name="acknowledgedBy">确认人，可省略。</param>
    PointAlarmRecord? AcknowledgePoint(string pointName, string? acknowledgedBy = null);

    /// <summary>
    /// 确认当前全部未复归报警。
    /// </summary>
    /// <param name="acknowledgedBy">确认人，可省略。</param>
    IReadOnlyList<PointAlarmRecord> AcknowledgeAll(string? acknowledgedBy = null);

    /// <summary>
    /// 把活动报警指派给责任人。记录不存在或已复归时抛出。
    /// </summary>
    /// <param name="id">报警记录标识。</param>
    /// <param name="assignee">责任人。空字符串表示取消指派。</param>
    PointAlarmRecord Assign(Guid id, string? assignee);

    /// <summary>
    /// 搁置一条活动报警，直到 <paramref name="until"/>。到期后若点仍越限，重新进入活动队列。
    /// </summary>
    /// <param name="id">报警记录标识。</param>
    /// <param name="until">搁置截止时间，必须晚于当前时间。</param>
    PointAlarmRecord Shelve(Guid id, DateTimeOffset until);

    /// <summary>
    /// 解除搁置。点仍越限时回到活动队列。
    /// </summary>
    /// <param name="id">报警记录标识。</param>
    PointAlarmRecord Unshelve(Guid id);

    /// <summary>
    /// 抑制指定点：期间该点越限不进入活动队列。已有活动记录会被标为搁置。
    /// </summary>
    /// <param name="pointName">短名或限定名。</param>
    void Suppress(string pointName);

    /// <summary>
    /// 解除点抑制。点当前越限时会立即产生活动报警。
    /// </summary>
    /// <param name="pointName">短名或限定名。</param>
    void Unsuppress(string pointName);

    /// <summary>指定点当前是否被抑制。</summary>
    bool IsSuppressed(string pointName);
}
