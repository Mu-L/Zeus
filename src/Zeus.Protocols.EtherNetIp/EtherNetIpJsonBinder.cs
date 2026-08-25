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
            builder.AddEtherNetIp(device.Name.Trim(), device.Channel.Trim(), null, Timeout(device), Points(device));
            return;
        }

        host!.AddEtherNetIp(device.Name.Trim(), device.Channel.Trim(), null, Timeout(device), Points(device));
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "responder")) == "ethernet-ip" ? new EtherNetIpSlaveResponder() : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device) => ZeusConfigurationOptions.Fingerprint(device.Options);

    private static TimeSpan? Timeout(DeviceConfiguration device)
        => ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds") is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static Action<EtherNetIpPointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var dataTypeValue = ZeusConfigurationOptions.GetString(point.Options, "dataType", "int");
                var dataType = ZeusConfigurationText.Normalize(dataTypeValue) switch
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
                    _ => throw new ZeusException($"EtherNet/IP dataType「{dataTypeValue}」不受支持。")
                };
                var tag = ZeusConfigurationOptions.GetString(point.Options, "tag");
                tag = string.IsNullOrWhiteSpace(tag) ? point.Name : tag.Trim();
                map.Tag(point.Name, tag, dataType, ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale"), ZeusConfigurationText.CreateAlarmLimits(point));
                if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
                {
                    map.Writable(point.Name);
                }
            }
        };
}
