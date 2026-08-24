using System.Globalization;

namespace Zeus;

/// <summary>
/// 点表报警限。低于下限触发低报，高于上限触发高报；等于阈值仍视为正常。
/// </summary>
public sealed class PointAlarmLimits
{
    /// <summary>
    /// 创建报警限。至少需要提供一个阈值。
    /// </summary>
    /// <param name="low">低报阈值。值低于该阈值时为 <see cref="PointAlarmState.Low"/>。</param>
    /// <param name="high">高报阈值。值高于该阈值时为 <see cref="PointAlarmState.High"/>。</param>
    /// <param name="deadband">回差。已处于高/低报时，必须回到阈值内侧该距离才复归，避免在阈值附近抖动。</param>
    /// <param name="severity">严重等级，默认警告。</param>
    /// <param name="area">区域或工段，供列表过滤与到人；可省略。</param>
    /// <param name="defaultAssignee">新报警的默认责任人；可省略。</param>
    public PointAlarmLimits(
        double? low = null,
        double? high = null,
        double deadband = 0,
        PointAlarmSeverity severity = PointAlarmSeverity.Warning,
        string? area = null,
        string? defaultAssignee = null)
    {
        if (low is null && high is null)
        {
            throw new ZeusException("报警限至少需要配置 low 或 high 中的一个。");
        }

        ValidateFinite(low, nameof(low));
        ValidateFinite(high, nameof(high));
        if (low > high)
        {
            throw new ZeusException("报警下限不能高于报警上限。");
        }

        if (deadband < 0 || !double.IsFinite(deadband))
        {
            throw new ZeusException("报警回差必须是大于或等于 0 的有限数值。");
        }

        if (!Enum.IsDefined(severity))
        {
            throw new ZeusException("报警严重等级无效。");
        }

        Low = low;
        High = high;
        Deadband = deadband;
        Severity = severity;
        Area = string.IsNullOrWhiteSpace(area) ? null : area.Trim();
        DefaultAssignee = string.IsNullOrWhiteSpace(defaultAssignee) ? null : defaultAssignee.Trim();
    }

    /// <summary>低报阈值。</summary>
    public double? Low { get; }

    /// <summary>高报阈值。</summary>
    public double? High { get; }

    /// <summary>回差。已报警时需越过阈值内侧该距离才复归。</summary>
    public double Deadband { get; }

    /// <summary>该点越限时的严重等级。</summary>
    public PointAlarmSeverity Severity { get; }

    /// <summary>区域或工段。未配置时为 <c>null</c>。</summary>
    public string? Area { get; }

    /// <summary>新报警的默认责任人。未配置时为 <c>null</c>。</summary>
    public string? DefaultAssignee { get; }

    /// <summary>
    /// 根据当前点值判断报警状态。无历史状态时按瞬时越限计算。
    /// </summary>
    /// <param name="value">当前点值。</param>
    public PointAlarmState Evaluate(object? value) => Evaluate(value, PointAlarmState.Unknown);

    /// <summary>
    /// 根据当前点值与上一状态判断报警。配置了 <see cref="Deadband"/> 时，复归需要越过回差。
    /// </summary>
    /// <param name="value">当前点值。</param>
    /// <param name="previous">上一快照的报警状态。</param>
    public PointAlarmState Evaluate(object? value, PointAlarmState previous)
    {
        if (!TryReadNumber(value, out var number))
        {
            return PointAlarmState.Unknown;
        }

        if (previous == PointAlarmState.High)
        {
            if (High is { } highHold && number > highHold - Deadband)
            {
                return PointAlarmState.High;
            }
        }
        else if (High is { } highEnter && number > highEnter)
        {
            return PointAlarmState.High;
        }

        if (previous == PointAlarmState.Low)
        {
            if (Low is { } lowHold && number < lowHold + Deadband)
            {
                return PointAlarmState.Low;
            }
        }
        else if (Low is { } lowEnter && number < lowEnter)
        {
            return PointAlarmState.Low;
        }

        return PointAlarmState.Normal;
    }

    private static void ValidateFinite(double? value, string name)
    {
        if (value is { } number && !double.IsFinite(number))
        {
            throw new ZeusException($"报警限 {name} 必须是有限数值。");
        }
    }

    private static bool TryReadNumber(object? value, out double number)
    {
        number = 0;
        if (value is null or bool or char)
        {
            return false;
        }

        try
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(number);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
