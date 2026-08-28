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

        var pointMap = new EtherNetIpPointMap();
        for (var i = 0; i < device.Points.Count; i++)
        {
            ValidatePoint(device.Points[i], $"{path}.points[{i}]", pointMap);
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
                var dataType = ParseDataType(dataTypeValue, $"point {point.Name}.options.dataType");
                var tag = ZeusConfigurationOptions.GetString(point.Options, "tag");
                tag = string.IsNullOrWhiteSpace(tag) ? point.Name : tag.Trim();
                map.Tag(point.Name, tag, dataType, ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale"), ZeusConfigurationText.CreateAlarmLimits(point));
                if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
                {
                    map.Writable(point.Name);
                }
            }
        };

    private static EtherNetIpDataType ParseDataType(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
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
            _ => throw new ZeusException($"{path}「{value}」不受支持。EtherNet/IP 可选 bool、sint、int、dint、lint、usint、uint、udint、ulint、real、lreal。")
        };

    private static void ValidatePoint(PointConfiguration point, string path, EtherNetIpPointMap pointMap)
    {
        ZeusConfigurationText.EnsureName(point.Name, path);
        var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "int", path), $"{path}.options.dataType");
        var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale", path);
        if (scale is { } factor && (factor <= 0 || !double.IsFinite(factor)))
        {
            throw new ZeusException($"{path}.options.scale 必须是大于 0 的有限数值。");
        }

        ZeusConfigurationText.ValidatePointAlarms(point, path);
        if (dataType == EtherNetIpDataType.Bool)
        {
            if (scale is not null)
            {
                throw new ZeusException($"{path}.options.scale 只能用于 EtherNet/IP 数值点。");
            }

            if (ZeusConfigurationOptions.GetNullableDouble(point.Options, "lowAlarmLimit", path) is not null
                || ZeusConfigurationOptions.GetNullableDouble(point.Options, "highAlarmLimit", path) is not null)
            {
                throw new ZeusException($"{path}.options.lowAlarmLimit 或 highAlarmLimit 只能用于 EtherNet/IP 数值点。");
            }
        }

        var tag = ZeusConfigurationOptions.GetString(point.Options, "tag", path: path);
        tag = string.IsNullOrWhiteSpace(tag) ? point.Name : tag.Trim();
        pointMap.Tag(point.Name, tag, dataType, scale, ZeusConfigurationText.CreateAlarmLimits(point));
        if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable", path: path))
        {
            pointMap.Writable(point.Name);
        }
    }
}
