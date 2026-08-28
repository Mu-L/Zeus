namespace Zeus;

/// <summary>SNMP v2c 的 JSON 设备与虚拟 Agent 绑定。由配置核心探测本程序集后登记。</summary>
public sealed class SnmpJsonBinder : IZeusJsonBinder
{
    /// <inheritdoc />
    public IReadOnlyList<string> DeviceTypes { get; } = ["snmp"];

    /// <inheritdoc />
    public IReadOnlyList<string> ResponderTypes { get; } = ["snmp"];

    /// <inheritdoc />
    public void ValidateDevice(DeviceConfiguration device, string path)
    {
        if (ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds", path) is <= 0)
        {
            throw new ZeusException($"{path}.options.timeoutMilliseconds 必须大于 0。");
        }

        foreach (var point in device.Points)
        {
            var oid = ZeusConfigurationOptions.GetString(point.Options, "oid");
            if (string.IsNullOrWhiteSpace(oid))
            {
                throw new ZeusException($"点 {point.Name}.options.oid 不能为空。");
            }

            var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "text"), $"点 {point.Name}.options.dataType");
            ValidateNumericOptions(point, dataType, $"点 {point.Name}");

            // 与原先装载器一致：非法 OID 在装载期抛协议异常，而不是拖到采集。
            _ = SnmpValue.ObjectIdentifier(oid);
        }
    }

    /// <inheritdoc />
    public void ValidateResponder(ChannelConfiguration channel, string path)
    {
        if (string.IsNullOrWhiteSpace(ZeusConfigurationOptions.GetString(channel.Options, "snmpCommunity", "public", path)))
        {
            throw new ZeusException($"{path}.options.snmpCommunity 不能为空。");
        }
    }

    /// <inheritdoc />
    public void ApplyDevice(DeviceConfiguration device, ZeusHostBuilder? builder = null, IZeusHost? host = null)
    {
        if (builder is not null)
        {
            builder.AddSnmp(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
            return;
        }

        host!.AddSnmp(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "responder")) == "snmp"
            ? new SnmpAgentResponder(
                community: ZeusConfigurationOptions.GetString(channel.Options, "snmpCommunity", "public")!,
                writeCommunity: ZeusConfigurationOptions.GetString(channel.Options, "snmpWriteCommunity"))
            : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => ZeusConfigurationOptions.Fingerprint(device.Options);

    private static TimeSpan? Timeout(DeviceConfiguration device)
        => ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds") is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static SnmpOptions Options(DeviceConfiguration device)
        => new()
        {
            Community = ZeusConfigurationOptions.GetString(device.Options, "snmpCommunity", "public")!.Trim(),
            WriteCommunity = ZeusConfigurationOptions.GetString(device.Options, "snmpWriteCommunity") is { } writeCommunity && !string.IsNullOrWhiteSpace(writeCommunity) ? writeCommunity.Trim() : null,
            InitialRequestId = ZeusConfigurationOptions.GetInt32(device.Options, "snmpInitialRequestId", 1)
        };

    private static Action<SnmpPointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var oid = ZeusConfigurationOptions.RequireString(point.Options, "oid");
                var alarmLimits = ZeusConfigurationText.CreateAlarmLimits(point);
                var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "text"), $"point {point.Name}.options.dataType");
                switch (dataType)
                {
                    case SnmpDataType.Integer:
                        map.Integer(point.Name, oid, ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale"), alarmLimits);
                        break;
                    case SnmpDataType.Gauge32:
                        map.Gauge32(point.Name, oid, ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale"), alarmLimits);
                        break;
                    case SnmpDataType.Counter32:
                        map.Counter32(point.Name, oid, ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale"), alarmLimits);
                        break;
                    case SnmpDataType.TimeTicks:
                        map.TimeTicks(point.Name, oid, ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale"), alarmLimits);
                        break;
                    case SnmpDataType.OctetString:
                        map.OctetString(point.Name, oid);
                        break;
                    case SnmpDataType.ObjectIdentifier:
                        map.ObjectIdentifier(point.Name, oid);
                        break;
                    case SnmpDataType.IpAddress:
                        map.IpAddress(point.Name, oid);
                        break;
                    case SnmpDataType.Text:
                        map.Text(point.Name, oid);
                        break;
                    default:
                        throw new ZeusException($"不支持的 SNMP 数据类型：{dataType}。");
                }

                if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
                {
                    map.Writable(point.Name);
                }
            }
        };

    private static SnmpDataType ParseDataType(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "" or "text" => SnmpDataType.Text,
            "integer" => SnmpDataType.Integer,
            "gauge32" => SnmpDataType.Gauge32,
            "counter32" => SnmpDataType.Counter32,
            "timeticks" => SnmpDataType.TimeTicks,
            "octet-string" => SnmpDataType.OctetString,
            "oid" => SnmpDataType.ObjectIdentifier,
            "ip-address" => SnmpDataType.IpAddress,
            _ => throw new ZeusException($"{path}「{value}」不受支持。SNMP 可选 text、integer、gauge32、counter32、timeticks、octet-string、oid、ip-address。")
        };

    private static void ValidateNumericOptions(PointConfiguration point, SnmpDataType dataType, string path)
    {
        var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale", path);
        if (scale is { } factor && (factor <= 0 || !double.IsFinite(factor)))
        {
            throw new ZeusException($"{path}.options.scale 必须是大于 0 的有限数值。");
        }

        ZeusConfigurationText.ValidatePointAlarms(point, path);
        if (SnmpCodec.IsNumeric(dataType))
        {
            return;
        }

        if (scale is not null)
        {
            throw new ZeusException($"{path}.options.scale 只能用于 SNMP 数值点。");
        }

        if (ZeusConfigurationOptions.GetNullableDouble(point.Options, "lowAlarmLimit", path) is not null
            || ZeusConfigurationOptions.GetNullableDouble(point.Options, "highAlarmLimit", path) is not null)
        {
            throw new ZeusException($"{path}.options.lowAlarmLimit 或 highAlarmLimit 只能用于 SNMP 数值点。");
        }
    }
}
