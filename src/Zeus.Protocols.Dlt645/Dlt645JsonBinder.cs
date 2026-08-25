namespace Zeus;

/// <summary>DL/T 645 的 JSON 设备与虚拟表计绑定。由配置核心探测本程序集后登记。</summary>
public sealed class Dlt645JsonBinder : IZeusJsonBinder
{
    /// <inheritdoc />
    public IReadOnlyList<string> DeviceTypes { get; } = ["dlt645"];

    /// <inheritdoc />
    public IReadOnlyList<string> ResponderTypes { get; } = ["dlt645"];

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
            builder.AddDlt645(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
            return;
        }

        host!.AddDlt645(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "responder")) == "dlt645"
            ? new Dlt645SlaveResponder(ZeusConfigurationOptions.GetString(channel.Options, "meterAddress", "000000000001")!)
            : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => ZeusConfigurationOptions.Fingerprint(device.Options);

    private static TimeSpan? Timeout(DeviceConfiguration device)
        => ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds") is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static Dlt645Options Options(DeviceConfiguration device)
        => new()
        {
            MeterAddress = ZeusConfigurationOptions.GetString(device.Options, "meterAddress", "000000000001")!.Trim(),
            WakeUpPreambleCount = ZeusConfigurationOptions.GetInt32(device.Options, "wakeUpPreambleCount", 4),
            Password = ZeusConfigurationOptions.GetString(device.Options, "password", "00000000")!.Trim(),
            OperatorCode = ZeusConfigurationOptions.GetString(device.Options, "operatorCode", "00000000")!.Trim()
        };

    private static Action<Dlt645PointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var dataType = ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(point.Options, "dataType", "bcd"));
                var id = checked((uint)ZeusConfigurationOptions.GetInt32(point.Options, "address"));
                var dataLength = ZeusConfigurationOptions.GetInt32(point.Options, "dataLength", 4);
                if (dataType is "raw")
                {
                    map.RawBytes(point.Name, id, dataLength);
                }
                else
                {
                    map.Bcd(point.Name, id, dataLength, ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale") ?? 0.01, ZeusConfigurationText.CreateAlarmLimits(point));
                }

                if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
                {
                    map.Writable(point.Name);
                }
            }
        };
}
