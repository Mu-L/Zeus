using System.Text;

namespace Zeus;

/// <summary>MQTT 的 JSON 设备与虚拟 Broker 绑定。由配置核心探测本程序集后登记。</summary>
public sealed class MqttJsonBinder : IZeusJsonBinder
{
    /// <inheritdoc />
    public IReadOnlyList<string> DeviceTypes { get; } = ["mqtt"];

    /// <inheritdoc />
    public IReadOnlyList<string> ResponderTypes { get; } = ["mqtt"];

    /// <inheritdoc />
    public void ValidateDevice(DeviceConfiguration device, string path)
    {
        if (ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds", path) is <= 0)
        {
            throw new ZeusException($"{path}.options.timeoutMilliseconds 必须大于 0。");
        }

        ParseQos(ZeusConfigurationOptions.GetString(device.Options, "mqttWillQos", "0", path), $"{path}.options.mqttWillQos");
        ValidateOptions(Options(device, path), $"{path}.options");

        for (var i = 0; i < device.Points.Count; i++)
        {
            var point = device.Points[i];
            var pointPath = $"{path}.points[{i}]";
            var topic = ZeusConfigurationOptions.GetString(point.Options, "topic");
            topic = string.IsNullOrWhiteSpace(topic) ? point.Name : topic.Trim();
            if (topic.Contains('+') || topic.Contains('#'))
            {
                throw new ZeusException($"{path} 点 {point.Name} 的 topic 不能包含 MQTT 通配符。");
            }

            var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "text", pointPath), $"{pointPath}.options.dataType");
            ValidatePointOptions(point, dataType, pointPath);
            ParseQos(ZeusConfigurationOptions.GetString(point.Options, "mqttQos", "0", pointPath), $"{pointPath}.options.mqttQos");
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
            builder.AddMqtt(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
            return;
        }

        host!.AddMqtt(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "responder")) == "mqtt" ? new MqttBrokerResponder() : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => ZeusConfigurationOptions.Fingerprint(device.Options);

    private static TimeSpan? Timeout(DeviceConfiguration device)
        => ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds") is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static MqttOptions Options(DeviceConfiguration device, string? path = null)
        => new()
        {
            ClientId = ZeusConfigurationOptions.GetString(device.Options, "mqttClientId", path: path),
            Username = ZeusConfigurationOptions.GetString(device.Options, "mqttUsername", path: path),
            Password = ZeusConfigurationOptions.GetString(device.Options, "mqttPassword", path: path),
            KeepAliveSeconds = ReadKeepAliveSeconds(device, path),
            CleanSession = ZeusConfigurationOptions.GetBoolean(device.Options, "mqttCleanSession", true, path),
            WillTopic = ZeusConfigurationOptions.GetString(device.Options, "mqttWillTopic", path: path),
            WillPayload = ZeusConfigurationOptions.GetString(device.Options, "mqttWillPayload", path: path) is { } payload ? Encoding.UTF8.GetBytes(payload) : null,
            WillQualityOfService = ParseQos(
                ZeusConfigurationOptions.GetString(device.Options, "mqttWillQos", "0", path),
                path is null ? "device.options.mqttWillQos" : $"{path}.options.mqttWillQos"),
            WillRetain = ZeusConfigurationOptions.GetBoolean(device.Options, "mqttWillRetain", path: path),
            MaximumPacketSize = ZeusConfigurationOptions.GetInt32(device.Options, "mqttMaximumPacketSize", 1024 * 1024, path),
            AutomaticKeepAlive = ZeusConfigurationOptions.GetBoolean(device.Options, "mqttAutomaticKeepAlive", true, path),
            AutomaticReconnect = ZeusConfigurationOptions.GetBoolean(device.Options, "mqttAutomaticReconnect", true, path)
        };

    private static Action<MqttPointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var topic = ZeusConfigurationOptions.GetString(point.Options, "topic");
                topic = string.IsNullOrWhiteSpace(topic) ? point.Name : topic.Trim();
                var alarmLimits = ZeusConfigurationText.CreateAlarmLimits(point);
                switch (ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "text"), $"point {point.Name}.options.dataType"))
                {
                    case MqttDataType.Boolean:
                        map.Boolean(point.Name, topic);
                        break;
                    case MqttDataType.Int32:
                        map.Int32(point.Name, topic, alarmLimits);
                        break;
                    case MqttDataType.Int64:
                        map.Int64(point.Name, topic, alarmLimits);
                        break;
                    case MqttDataType.Double:
                        map.Double(point.Name, topic, alarmLimits);
                        break;
                    case MqttDataType.Bytes:
                        map.Bytes(point.Name, topic);
                        break;
                    case MqttDataType.Text:
                        map.Text(point.Name, topic);
                        break;
                }

                if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
                {
                    map.Writable(point.Name);
                }

                map.WithQualityOfService(point.Name, ParseQos(ZeusConfigurationOptions.GetString(point.Options, "mqttQos", "0"), $"point {point.Name}.options.mqttQos"));
                map.Retained(point.Name, ZeusConfigurationOptions.GetBoolean(point.Options, "mqttRetain", true));
            }
        };

    private static MqttDataType ParseDataType(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "" or "text" => MqttDataType.Text,
            "boolean" => MqttDataType.Boolean,
            "int32" => MqttDataType.Int32,
            "int64" => MqttDataType.Int64,
            "double" => MqttDataType.Double,
            "bytes" => MqttDataType.Bytes,
            _ => throw new ZeusException($"{path}「{value}」不受支持。MQTT 可选 text、boolean、int32、int64、double、bytes。")
        };

    private static MqttQualityOfService ParseQos(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "" or "0" => MqttQualityOfService.AtMostOnce,
            "1" => MqttQualityOfService.AtLeastOnce,
            "2" => MqttQualityOfService.ExactlyOnce,
            _ => throw new ZeusException($"{path}「{value}」不受支持。MQTT QoS 可选 0、1、2。")
        };

    private static ushort ReadKeepAliveSeconds(DeviceConfiguration device, string? path)
    {
        var keepAliveSeconds = ZeusConfigurationOptions.GetInt32(device.Options, "mqttKeepAliveSeconds", 60, path);
        if (keepAliveSeconds is < ushort.MinValue or > ushort.MaxValue)
        {
            var label = path is null ? "mqttKeepAliveSeconds" : $"{path}.options.mqttKeepAliveSeconds";
            throw new ZeusException($"{label} 必须介于 0 与 65535 之间。");
        }

        return (ushort)keepAliveSeconds;
    }

    private static void ValidateOptions(MqttOptions options, string path)
    {
        try
        {
            MqttCodec.ValidateOptions(options);
        }
        catch (ZeusException ex)
        {
            throw new ZeusException($"{path} 无效：{ex.Message}", ex);
        }
    }

    private static void ValidatePointOptions(PointConfiguration point, MqttDataType dataType, string path)
    {
        if (ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale", path) is not null)
        {
            throw new ZeusException($"{path}.options.scale 不适用于 MQTT 点。MQTT 载荷按文本直接解析，不执行 scale 换算。");
        }

        ZeusConfigurationText.ValidatePointAlarms(point, path);
        if (dataType is MqttDataType.Int32 or MqttDataType.Int64 or MqttDataType.Double)
        {
            return;
        }

        if (ZeusConfigurationOptions.GetNullableDouble(point.Options, "lowAlarmLimit", path) is not null
            || ZeusConfigurationOptions.GetNullableDouble(point.Options, "highAlarmLimit", path) is not null
            || ZeusConfigurationOptions.Contains(point.Options, "deadband"))
        {
            throw new ZeusException($"{path}.options.lowAlarmLimit、highAlarmLimit 或 deadband 只能用于 MQTT 数值点。");
        }
    }
}
