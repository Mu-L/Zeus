namespace Zeus;

/// <summary>IEC 60870-5-104 的 JSON 设备与虚拟站绑定。由配置核心探测本程序集后登记。</summary>
public sealed class Iec104JsonBinder : IZeusJsonBinder
{
    /// <inheritdoc />
    public IReadOnlyList<string> DeviceTypes { get; } = ["iec104"];

    /// <inheritdoc />
    public IReadOnlyList<string> ResponderTypes { get; } = ["iec104"];

    /// <inheritdoc />
    public void ValidateDevice(DeviceConfiguration device, string path)
    {
        if (ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds", path) is <= 0)
        {
            throw new ZeusException($"{path}.options.timeoutMilliseconds 必须大于 0。");
        }

        ValidateOptions(Options(device, path), $"{path}.options");

        for (var i = 0; i < device.Points.Count; i++)
        {
            var point = device.Points[i];
            var pointPath = $"{path}.points[{i}]";
            ZeusConfigurationText.EnsureName(point.Name, pointPath);
            ValidatePoint(point, pointPath);
        }
    }

    /// <inheritdoc />
    public void ValidateResponder(ChannelConfiguration channel, string path)
    {
        ValidateOptions(
            new Iec104Options { CommonAddress = ZeusConfigurationOptions.GetInt32(channel.Options, "commonAddress", 1, path) },
            $"{path}.options");
    }

    /// <inheritdoc />
    public void ApplyDevice(DeviceConfiguration device, ZeusHostBuilder? builder = null, IZeusHost? host = null)
    {
        if (builder is not null)
        {
            builder.AddIec104(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
            return;
        }

        host!.AddIec104(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "responder")) == "iec104"
            ? new Iec104SlaveResponder(new Iec104Options { CommonAddress = ZeusConfigurationOptions.GetInt32(channel.Options, "commonAddress", 1) })
            : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => ZeusConfigurationOptions.Fingerprint(device.Options);

    private static TimeSpan? Timeout(DeviceConfiguration device)
        => ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds") is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static Iec104Options Options(DeviceConfiguration device, string? path = null)
        => new()
        {
            CommonAddress = ZeusConfigurationOptions.GetInt32(device.Options, "commonAddress", 1, path),
            OriginatorAddress = ZeusConfigurationOptions.GetInt32(device.Options, "originatorAddress", path: path),
            InterrogationQualifier = ZeusConfigurationOptions.GetInt32(device.Options, "interrogationQualifier", 20, path),
            T1 = TimeSpan.FromMilliseconds(ZeusConfigurationOptions.GetInt32(device.Options, "t1Milliseconds", 15000, path)),
            T2 = TimeSpan.FromMilliseconds(ZeusConfigurationOptions.GetInt32(device.Options, "t2Milliseconds", 10000, path)),
            T3 = TimeSpan.FromMilliseconds(ZeusConfigurationOptions.GetInt32(device.Options, "t3Milliseconds", 20000, path)),
            MaxUnacknowledgedIFrames = ZeusConfigurationOptions.GetInt32(device.Options, "maxUnacknowledgedIFrames", 12, path),
            AcknowledgeWindow = ZeusConfigurationOptions.GetInt32(device.Options, "acknowledgeWindow", 8, path)
        };

    private static Action<Iec104PointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "scaled"), $"point {point.Name}.options.dataType");
                var alarmLimits = ZeusConfigurationText.CreateAlarmLimits(point);
                var address = ZeusConfigurationOptions.GetInt32(point.Options, "address");
                var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale");
                switch (dataType)
                {
                    case Iec104DataType.SinglePoint:
                        map.SinglePoint(point.Name, address);
                        break;
                    case Iec104DataType.Normalized:
                        map.Normalized(point.Name, address, scale, alarmLimits);
                        break;
                    case Iec104DataType.ShortFloat:
                        map.ShortFloat(point.Name, address, scale, alarmLimits);
                        break;
                    case Iec104DataType.Scaled:
                        map.Scaled(point.Name, address, scale, alarmLimits);
                        break;
                    default:
                        throw new ZeusException($"不支持的 IEC104 数据类型：{dataType}。");
                }

                if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
                {
                    map.Writable(point.Name);
                }
            }
        };

    private static Iec104DataType ParseDataType(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "" or "scaled" => Iec104DataType.Scaled,
            "single-point" => Iec104DataType.SinglePoint,
            "normalized" => Iec104DataType.Normalized,
            "short-float" => Iec104DataType.ShortFloat,
            _ => throw new ZeusException($"{path}「{value}」不受支持。IEC104 可选 scaled、single-point、normalized、short-float。")
        };

    private static void ValidateOptions(Iec104Options options, string path)
    {
        try
        {
            Iec104Codec.ValidateOptions(options);
        }
        catch (ZeusException ex)
        {
            throw new ZeusException($"{path} 无效：{ex.Message}", ex);
        }
    }

    private static void ValidatePoint(PointConfiguration point, string path)
    {
        var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "scaled", path), $"{path}.options.dataType");
        var address = ZeusConfigurationOptions.GetInt32(point.Options, "address", path: path);
        if (address is < 0 or > 0xFFFFFF)
        {
            throw new ZeusException($"{path}.options.address 必须介于 0 与 16777215 之间。");
        }

        var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale", path);
        if (scale is { } factor && (factor <= 0 || !double.IsFinite(factor)))
        {
            throw new ZeusException($"{path}.options.scale 必须是大于 0 的有限数值。");
        }

        ZeusConfigurationText.ValidatePointAlarms(point, path);
        if (dataType != Iec104DataType.SinglePoint)
        {
            return;
        }

        if (scale is not null)
        {
            throw new ZeusException($"{path}.options.scale 只能用于 IEC104 数值点。");
        }

        if (ZeusConfigurationOptions.GetNullableDouble(point.Options, "lowAlarmLimit", path) is not null
            || ZeusConfigurationOptions.GetNullableDouble(point.Options, "highAlarmLimit", path) is not null)
        {
            throw new ZeusException($"{path}.options.lowAlarmLimit 或 highAlarmLimit 只能用于 IEC104 数值点。");
        }
    }
}
