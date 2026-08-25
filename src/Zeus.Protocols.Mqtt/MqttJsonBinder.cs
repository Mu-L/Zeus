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

        foreach (var point in device.Points)
        {
            var topic = ZeusConfigurationOptions.GetString(point.Options, "topic");
            topic = string.IsNullOrWhiteSpace(topic) ? point.Name : topic.Trim();
            if (topic.Contains('+') || topic.Contains('#'))
            {
                throw new ZeusException($"{path} 点 {point.Name} 的 topic 不能包含 MQTT 通配符。");
            }
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

    private static MqttOptions Options(DeviceConfiguration device)
        => new()
        {
            ClientId = ZeusConfigurationOptions.GetString(device.Options, "mqttClientId"),
            Username = ZeusConfigurationOptions.GetString(device.Options, "mqttUsername"),
            Password = ZeusConfigurationOptions.GetString(device.Options, "mqttPassword"),
            KeepAliveSeconds = checked((ushort)ZeusConfigurationOptions.GetInt32(device.Options, "mqttKeepAliveSeconds", 60)),
            CleanSession = ZeusConfigurationOptions.GetBoolean(device.Options, "mqttCleanSession", true),
            WillTopic = ZeusConfigurationOptions.GetString(device.Options, "mqttWillTopic"),
            WillPayload = ZeusConfigurationOptions.GetString(device.Options, "mqttWillPayload") is { } payload ? Encoding.UTF8.GetBytes(payload) : null,
            WillQualityOfService = ParseQos(ZeusConfigurationOptions.GetString(device.Options, "mqttWillQos", "0")),
            WillRetain = ZeusConfigurationOptions.GetBoolean(device.Options, "mqttWillRetain"),
            MaximumPacketSize = ZeusConfigurationOptions.GetInt32(device.Options, "mqttMaximumPacketSize", 1024 * 1024),
            AutomaticKeepAlive = ZeusConfigurationOptions.GetBoolean(device.Options, "mqttAutomaticKeepAlive", true),
            AutomaticReconnect = ZeusConfigurationOptions.GetBoolean(device.Options, "mqttAutomaticReconnect", true)
        };

    private static Action<MqttPointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var topic = ZeusConfigurationOptions.GetString(point.Options, "topic");
                topic = string.IsNullOrWhiteSpace(topic) ? point.Name : topic.Trim();
                var alarmLimits = ZeusConfigurationText.CreateAlarmLimits(point);
                switch (ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(point.Options, "dataType", "text")))
                {
                    case "boolean":
                        map.Boolean(point.Name, topic);
                        break;
                    case "int32":
                        map.Int32(point.Name, topic, alarmLimits);
                        break;
                    case "int64":
                        map.Int64(point.Name, topic, alarmLimits);
                        break;
                    case "double":
                        map.Double(point.Name, topic, alarmLimits);
                        break;
                    case "bytes":
                        map.Bytes(point.Name, topic);
                        break;
                    default:
                        map.Text(point.Name, topic);
                        break;
                }

                if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
                {
                    map.Writable(point.Name);
                }

                map.WithQualityOfService(point.Name, ParseQos(ZeusConfigurationOptions.GetString(point.Options, "mqttQos", "0")));
                map.Retained(point.Name, ZeusConfigurationOptions.GetBoolean(point.Options, "mqttRetain", true));
            }
        };

    private static MqttQualityOfService ParseQos(string? value)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "1" => MqttQualityOfService.AtLeastOnce,
            "2" => MqttQualityOfService.ExactlyOnce,
            _ => MqttQualityOfService.AtMostOnce
        };
}
