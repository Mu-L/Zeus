namespace Zeus;

/// <summary>
/// JSON 配置文本规范化。协议绑定与装载器共用，避免各包复制一份大小写/下划线处理。
/// </summary>
public static class ZeusConfigurationText
{
    /// <summary>
    /// 去掉空白并转小写。
    /// </summary>
    public static string Normalize(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// 校验名称非空。
    /// </summary>
    public static void EnsureName(string? name, string path)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ZeusException($"{path}.name 不能为空。");
        }
    }

    /// <summary>
    /// 校验报警限为有限数值。
    /// </summary>
    public static void ValidateAlarmLimit(double? value, string path)
    {
        if (value is { } number && !double.IsFinite(number))
        {
            throw new ZeusException($"{path} 必须是有限数值。");
        }
    }

    /// <summary>
    /// 校验点上的报警限对与回差。
    /// </summary>
    public static void ValidatePointAlarms(PointConfiguration point, string path)
    {
        var low = ZeusConfigurationOptions.GetNullableDouble(point.Options, "lowAlarmLimit", path);
        var high = ZeusConfigurationOptions.GetNullableDouble(point.Options, "highAlarmLimit", path);
        var deadband = ZeusConfigurationOptions.GetDouble(point.Options, "deadband", 0, path);
        ValidateAlarmLimit(low, $"{path}.options.lowAlarmLimit");
        ValidateAlarmLimit(high, $"{path}.options.highAlarmLimit");
        if (low > high)
        {
            throw new ZeusException($"{path}.options.lowAlarmLimit 不能高于 highAlarmLimit。");
        }

        if (deadband < 0 || !double.IsFinite(deadband))
        {
            throw new ZeusException($"{path}.options.deadband 必须是大于或等于 0 的有限数值。");
        }
    }

    /// <summary>
    /// 由 JSON 点生成报警限；未配置阈值时返回 <c>null</c>。
    /// </summary>
    public static PointAlarmLimits? CreateAlarmLimits(PointConfiguration point)
    {
        var low = ZeusConfigurationOptions.GetNullableDouble(point.Options, "lowAlarmLimit");
        var high = ZeusConfigurationOptions.GetNullableDouble(point.Options, "highAlarmLimit");
        return low is not null || high is not null
            ? new PointAlarmLimits(
                low,
                high,
                ZeusConfigurationOptions.GetDouble(point.Options, "deadband"),
                ParseSeverity(ZeusConfigurationOptions.GetString(point.Options, "alarmSeverity")),
                ZeusConfigurationOptions.GetString(point.Options, "alarmArea"),
                ZeusConfigurationOptions.GetString(point.Options, "alarmAssignee"))
            : null;
    }

    /// <summary>
    /// 解析 JSON 中的报警严重等级。省略或空为警告。
    /// </summary>
    public static PointAlarmSeverity ParseSeverity(string? value)
        => Normalize(value) switch
        {
            "" or "warning" => PointAlarmSeverity.Warning,
            "info" => PointAlarmSeverity.Info,
            "alarm" => PointAlarmSeverity.Alarm,
            "critical" => PointAlarmSeverity.Critical,
            _ => throw new ZeusException($"alarmSeverity「{value}」不受支持。可选 info、warning、alarm、critical。")
        };

    /// <summary>解析通道启动策略。</summary>
    public static ChannelStartupMode ParseStartupMode(string? value, string path = "startup")
        => Normalize(value) switch
        {
            "" or "required" => ChannelStartupMode.Required,
            "optional" => ChannelStartupMode.Optional,
            "degraded" or "degraded-allowed" => ChannelStartupMode.DegradedAllowed,
            _ => throw new ZeusException($"{path}「{value}」不受支持。可选 required、optional、degraded。")
        };

    /// <summary>
    /// 点表指纹，供热更新判断设备是否需要重建。
    /// </summary>
    public static string PointFingerprint(PointConfiguration point)
        => string.Join(':',
            point.Name,
            ZeusConfigurationOptions.Fingerprint(point.Options));
}
