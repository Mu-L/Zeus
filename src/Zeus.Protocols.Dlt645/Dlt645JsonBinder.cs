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
        if (device.TimeoutMilliseconds is <= 0)
        {
            throw new ZeusException($"{path}.timeoutMilliseconds 必须大于 0。");
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
        => ZeusConfigurationText.Normalize(channel.Responder) == "dlt645"
            ? new Dlt645SlaveResponder(channel.MeterAddress)
            : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => string.Join('|', device.MeterAddress, device.WakeUpPreambleCount, device.TimeoutMilliseconds);

    private static TimeSpan? Timeout(DeviceConfiguration device)
        => device.TimeoutMilliseconds is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static Dlt645Options Options(DeviceConfiguration device)
        => new()
        {
            MeterAddress = device.MeterAddress.Trim(),
            WakeUpPreambleCount = device.WakeUpPreambleCount,
            Password = device.Password.Trim(),
            OperatorCode = device.OperatorCode.Trim()
        };

    private static Action<Dlt645PointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var dataType = ZeusConfigurationText.Normalize(point.DataType);
                var id = checked((uint)point.Address);
                if (dataType is "raw")
                {
                    map.RawBytes(point.Name, id, point.DataLength);
                }
                else
                {
                    map.Bcd(point.Name, id, point.DataLength, point.Scale ?? 0.01, ZeusConfigurationText.CreateAlarmLimits(point));
                }

                if (point.Writable)
                {
                    map.Writable(point.Name);
                }
            }
        };
}
