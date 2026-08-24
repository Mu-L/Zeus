namespace Zeus;

/// <summary>Allen-Bradley EtherNet/IP 的 JSON 设备与虚拟从站绑定。由配置核心探测本程序集后登记。</summary>
public sealed class EtherNetIpJsonBinder : IZeusJsonBinder
{
    /// <inheritdoc />
    public IReadOnlyList<string> DeviceTypes { get; } = ["ethernet-ip"];

    /// <inheritdoc />
    public IReadOnlyList<string> ResponderTypes { get; } = ["ethernet-ip"];

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
            builder.AddEtherNetIp(device.Name.Trim(), device.Channel.Trim(), null, Timeout(device), Points(device));
            return;
        }

        host!.AddEtherNetIp(device.Name.Trim(), device.Channel.Trim(), null, Timeout(device), Points(device));
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(channel.Responder) == "ethernet-ip" ? new EtherNetIpSlaveResponder() : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device) => device.TimeoutMilliseconds?.ToString() ?? "";

    private static TimeSpan? Timeout(DeviceConfiguration device)
        => device.TimeoutMilliseconds is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static Action<EtherNetIpPointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var dataType = ZeusConfigurationText.Normalize(point.DataType) switch
                {
                    "bool" => EtherNetIpDataType.Bool,
                    "sint" => EtherNetIpDataType.SInt,
                    "int" => EtherNetIpDataType.Int,
                    "dint" => EtherNetIpDataType.DInt,
                    "lint" => EtherNetIpDataType.LInt,
                    "usint" => EtherNetIpDataType.USInt,
                    "uint" => EtherNetIpDataType.UInt,
                    "udint" => EtherNetIpDataType.UDInt,
                    "ulint" => EtherNetIpDataType.ULInt,
                    "real" => EtherNetIpDataType.Real,
                    "lreal" => EtherNetIpDataType.LReal,
                    _ => throw new ZeusException($"EtherNet/IP dataType「{point.DataType}」不受支持。")
                };
                var tag = string.IsNullOrWhiteSpace(point.Tag) ? point.Name : point.Tag.Trim();
                map.Tag(point.Name, tag, dataType, point.Scale, ZeusConfigurationText.CreateAlarmLimits(point));
                if (point.Writable)
                {
                    map.Writable(point.Name);
                }
            }
        };
}
