namespace Zeus;

/// <summary>
/// 点在某一时刻的快照。值与错误互斥：成功时 <see cref="Error"/> 为空。
/// </summary>
public sealed class PointSnapshot
{
    /// <summary>
    /// 创建快照。
    /// </summary>
    /// <param name="definition">静态定义。</param>
    /// <param name="value">当前值；失败时为上一次成功值或 <c>null</c>。</param>
    /// <param name="updatedAt">最近一次成功更新的时间。</param>
    /// <param name="error">最近一次采集失败的说明。</param>
    public PointSnapshot(PointDefinition definition, object? value, DateTimeOffset? updatedAt, string? error)
        : this(definition, value, updatedAt, error, previousAlarmState: null)
    {
    }

    /// <summary>
    /// 创建快照，并按上一报警状态计算回差。
    /// </summary>
    /// <param name="definition">静态定义。</param>
    /// <param name="value">当前值；失败时为上一次成功值或 <c>null</c>。</param>
    /// <param name="updatedAt">最近一次成功更新的时间。</param>
    /// <param name="error">最近一次采集失败的说明。</param>
    /// <param name="previousAlarmState">上一快照的报警状态；首次采集可省略。</param>
    public PointSnapshot(
        PointDefinition definition,
        object? value,
        DateTimeOffset? updatedAt,
        string? error,
        PointAlarmState? previousAlarmState)
        : this(
            definition,
            value,
            updatedAt,
            error,
            previousAlarmState,
            ResolveQuality(value, error),
            updatedAt)
    {
    }

    /// <summary>
    /// 创建快照，并保留数据质量与源侧采样时间。
    /// </summary>
    /// <param name="definition">静态定义。</param>
    /// <param name="value">当前值；失败时为上一次成功值或 <c>null</c>。</param>
    /// <param name="updatedAt">最近一次成功更新的时间。</param>
    /// <param name="error">最近一次采集失败的说明。</param>
    /// <param name="previousAlarmState">上一快照的报警状态；首次采集可省略。</param>
    /// <param name="quality">当前数据质量。</param>
    /// <param name="sourceTimestamp">数据源侧采样时间。</param>
    public PointSnapshot(
        PointDefinition definition,
        object? value,
        DateTimeOffset? updatedAt,
        string? error,
        PointAlarmState? previousAlarmState,
        PointQuality quality,
        DateTimeOffset? sourceTimestamp)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Value = value;
        UpdatedAt = updatedAt;
        Error = error;
        Quality = quality;
        SourceTimestamp = sourceTimestamp;
        Latency = updatedAt.HasValue && sourceTimestamp.HasValue
            ? updatedAt.Value - sourceTimestamp.Value
            : null;
        AlarmState = definition.AlarmLimits?.Evaluate(value, previousAlarmState ?? PointAlarmState.Unknown)
            ?? PointAlarmState.Disabled;
    }

    /// <summary>静态定义。</summary>
    public PointDefinition Definition { get; }

    /// <summary>当前值。</summary>
    public object? Value { get; }

    /// <summary>最近一次成功写入点表的时间。</summary>
    public DateTimeOffset? UpdatedAt { get; }

    /// <summary>数据源侧采样时间。未提供时通常等于本地更新时间。</summary>
    public DateTimeOffset? SourceTimestamp { get; }

    /// <summary>本地接收时间与源侧采样时间的差值；缺少源时间时为空。</summary>
    public TimeSpan? Latency { get; }

    /// <summary>最近一次失败原因；成功时为 <c>null</c>。</summary>
    public string? Error { get; }

    /// <summary>当前数据质量。</summary>
    public PointQuality Quality { get; }

    /// <summary>当前值相对于报警限的状态。采集失败时仍按保留的上一次成功值计算。</summary>
    public PointAlarmState AlarmState { get; }

    /// <summary>当前快照是否处于高报或低报。</summary>
    public bool IsAlarmed => AlarmState is PointAlarmState.Low or PointAlarmState.High;

    /// <summary>限定名，便于日志与绑定。</summary>
    public string QualifiedName => Definition.QualifiedName;

    /// <summary>
    /// 尝试把当前值读成有限 <see cref="double"/>。布尔与无法转换的类型返回 <c>false</c>。
    /// </summary>
    /// <param name="number">成功时的数值。</param>
    public bool TryGetDouble(out double number) => PointValueConvert.TryToDouble(Value, out number);

    private static PointQuality ResolveQuality(object? value, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return PointQuality.Bad;
        }

        return value is null ? PointQuality.Unknown : PointQuality.Good;
    }
}
