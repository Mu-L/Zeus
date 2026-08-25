namespace Zeus;

/// <summary>OPC UA 的 JSON 设备与虚拟 Server 绑定。由配置核心探测本程序集后登记。</summary>
public sealed class OpcUaJsonBinder : IZeusJsonBinder
{
    /// <inheritdoc />
    public IReadOnlyList<string> DeviceTypes { get; } = ["opcua", "opc-ua", "opc.ua"];

    /// <inheritdoc />
    public IReadOnlyList<string> ResponderTypes { get; } = ["opcua"];

    /// <inheritdoc />
    public void ValidateDevice(DeviceConfiguration device, string path)
    {
        if (ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds", path) is <= 0)
        {
            throw new ZeusException($"{path}.options.timeoutMilliseconds 必须大于 0。");
        }

        if (ZeusConfigurationOptions.GetNullableDouble(device.Options, "opcUaRequestedSessionTimeout", path) is <= 0)
        {
            throw new ZeusException($"{path}.options.opcUaRequestedSessionTimeout 必须大于 0。");
        }

        foreach (var point in device.Points)
        {
            var nodeId = ZeusConfigurationOptions.GetString(point.Options, "nodeId");
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                throw new ZeusException($"点 {point.Name}.options.nodeId 不能为空。");
            }

            // 与原先装载器一致：非法 NodeId 在装载期抛协议异常，而不是拖到采集。
            _ = OpcUaNodeId.Parse(nodeId);
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
            builder.AddOpcUa(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
            return;
        }

        host!.AddOpcUa(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "responder")) == "opcua"
            ? new OpcUaServerResponder(
                username: ZeusConfigurationOptions.GetString(channel.Options, "opcUaUsername"),
                password: ZeusConfigurationOptions.GetString(channel.Options, "opcUaPassword"))
            : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => ZeusConfigurationOptions.Fingerprint(device.Options);

    private static TimeSpan? Timeout(DeviceConfiguration device)
        => ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds") is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static OpcUaOptions Options(DeviceConfiguration device)
    {
        var username = ZeusConfigurationOptions.GetString(device.Options, "opcUaUsername");
        return new OpcUaOptions
        {
            EndpointUrl = ZeusConfigurationOptions.GetString(device.Options, "opcUaEndpointUrl"),
            ApplicationUri = ZeusConfigurationOptions.GetString(device.Options, "opcUaApplicationUri"),
            ApplicationName = ZeusConfigurationOptions.GetString(device.Options, "opcUaApplicationName"),
            SessionName = ZeusConfigurationOptions.GetString(device.Options, "opcUaSessionName"),
            RequestedSessionTimeout = ZeusConfigurationOptions.GetDouble(device.Options, "opcUaRequestedSessionTimeout", 60_000),
            Anonymous = string.IsNullOrWhiteSpace(username),
            Username = username,
            Password = ZeusConfigurationOptions.GetString(device.Options, "opcUaPassword"),
            AutomaticReconnect = ZeusConfigurationOptions.GetBoolean(device.Options, "opcUaAutomaticReconnect", true)
        };
    }

    private static Action<OpcUaPointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var nodeId = ZeusConfigurationOptions.RequireString(point.Options, "nodeId");
                var alarmLimits = ZeusConfigurationText.CreateAlarmLimits(point);
                var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "double"));
                map.Typed(point.Name, nodeId, dataType, ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale"), alarmLimits);
                if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
                {
                    map.Writable(point.Name);
                }
            }
        };

    private static OpcUaDataType ParseDataType(string? value)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "boolean" or "bool" => OpcUaDataType.Boolean,
            "sbyte" => OpcUaDataType.SByte,
            "byte" => OpcUaDataType.Byte,
            "int16" => OpcUaDataType.Int16,
            "uint16" => OpcUaDataType.UInt16,
            "int32" or "int" => OpcUaDataType.Int32,
            "uint32" => OpcUaDataType.UInt32,
            "int64" => OpcUaDataType.Int64,
            "uint64" => OpcUaDataType.UInt64,
            "float" or "real" => OpcUaDataType.Float,
            "string" or "text" => OpcUaDataType.String,
            "datetime" => OpcUaDataType.DateTime,
            "bytestring" or "bytes" => OpcUaDataType.ByteString,
            _ => OpcUaDataType.Double
        };
}
