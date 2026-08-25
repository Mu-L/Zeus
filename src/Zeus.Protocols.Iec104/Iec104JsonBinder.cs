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
    }

    /// <inheritdoc />
    public void ValidateResponder(ChannelConfiguration channel, string path)
    {
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

    private static Iec104Options Options(DeviceConfiguration device)
        => new()
        {
            CommonAddress = ZeusConfigurationOptions.GetInt32(device.Options, "commonAddress", 1),
            OriginatorAddress = ZeusConfigurationOptions.GetInt32(device.Options, "originatorAddress"),
            InterrogationQualifier = ZeusConfigurationOptions.GetInt32(device.Options, "interrogationQualifier", 20),
            T1 = TimeSpan.FromMilliseconds(ZeusConfigurationOptions.GetInt32(device.Options, "t1Milliseconds", 15000)),
            T2 = TimeSpan.FromMilliseconds(ZeusConfigurationOptions.GetInt32(device.Options, "t2Milliseconds", 10000)),
            T3 = TimeSpan.FromMilliseconds(ZeusConfigurationOptions.GetInt32(device.Options, "t3Milliseconds", 20000)),
            MaxUnacknowledgedIFrames = ZeusConfigurationOptions.GetInt32(device.Options, "maxUnacknowledgedIFrames", 12),
            AcknowledgeWindow = ZeusConfigurationOptions.GetInt32(device.Options, "acknowledgeWindow", 8)
        };

    private static Action<Iec104PointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var dataType = ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(point.Options, "dataType", "scaled"));
                var alarmLimits = ZeusConfigurationText.CreateAlarmLimits(point);
                var address = ZeusConfigurationOptions.GetInt32(point.Options, "address");
                var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale");
                switch (dataType)
                {
                    case "single-point":
                        map.SinglePoint(point.Name, address);
                        break;
                    case "normalized":
                        map.Normalized(point.Name, address, scale, alarmLimits);
                        break;
                    case "short-float":
                        map.ShortFloat(point.Name, address, scale, alarmLimits);
                        break;
                    default:
                        map.Scaled(point.Name, address, scale, alarmLimits);
                        break;
                }

                if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
                {
                    map.Writable(point.Name);
                }
            }
        };
}
