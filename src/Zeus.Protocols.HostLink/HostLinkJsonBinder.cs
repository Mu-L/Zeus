namespace Zeus;

/// <summary>Omron Host Link 的 JSON 设备与虚拟从站绑定。由配置核心探测本程序集后登记。</summary>
public sealed class HostLinkJsonBinder : IZeusJsonBinder
{
    /// <inheritdoc />
    public IReadOnlyList<string> DeviceTypes { get; } = ["omron-host-link"];

    /// <inheritdoc />
    public IReadOnlyList<string> ResponderTypes { get; } = ["host-link"];

    /// <inheritdoc />
    public void ValidateDevice(DeviceConfiguration device, string path)
    {
        var unitId = ZeusConfigurationOptions.GetInt32(device.Options, "unitId", 1, path);
        if (unitId is < 0 or > 31)
        {
            throw new ZeusException($"{path}.options.unitId 必须介于 0 与 31 之间。");
        }

        if (ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds", path) is <= 0)
        {
            throw new ZeusException($"{path}.options.timeoutMilliseconds 必须大于 0。");
        }

        for (var i = 0; i < device.Points.Count; i++)
        {
            var point = device.Points[i];
            var pointPath = $"{path}.points[{i}]";
            ZeusConfigurationText.EnsureName(point.Name, pointPath);
            if (string.IsNullOrWhiteSpace(ZeusConfigurationOptions.GetString(point.Options, "area", path: pointPath)))
            {
                throw new ZeusException($"{pointPath}.options.area 必须指定。");
            }
        }
    }

    /// <inheritdoc />
    public void ValidateResponder(ChannelConfiguration channel, string path)
    {
        var unitId = ZeusConfigurationOptions.GetInt32(channel.Options, "unitId", 1, path);
        if (unitId is < 0 or > 31)
        {
            throw new ZeusException($"{path}.options.unitId 必须介于 0 与 31 之间。");
        }
    }

    /// <inheritdoc />
    public void ApplyDevice(DeviceConfiguration device, ZeusHostBuilder? builder = null, IZeusHost? host = null)
    {
        if (builder is not null)
        {
            builder.AddOmronHostLink(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
            return;
        }

        host!.AddOmronHostLink(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "responder")) == "host-link"
            ? new HostLinkSlaveResponder((byte)ZeusConfigurationOptions.GetInt32(channel.Options, "unitId", 1))
            : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => ZeusConfigurationOptions.Fingerprint(device.Options);

    private static TimeSpan? Timeout(DeviceConfiguration device)
        => ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds") is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static HostLinkOptions Options(DeviceConfiguration device)
        => new()
        {
            UnitNumber = (byte)ZeusConfigurationOptions.GetInt32(device.Options, "unitId", 1),
            WordOrder = ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(device.Options, "wordOrder", "high-word-first")) == "low-word-first"
                ? HostLinkWordOrder.LowWordFirst
                : HostLinkWordOrder.HighWordFirst
        };

    private static Action<HostLinkPointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var area = ParseArea(ZeusConfigurationOptions.GetString(point.Options, "area"));
                var dataType = ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(point.Options, "dataType", "word"));
                var address = (ushort)ZeusConfigurationOptions.GetInt32(point.Options, "address");
                var bitOffset = (byte)ZeusConfigurationOptions.GetInt32(point.Options, "bit");
                var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale");
                if (dataType is "bit")
                {
                    map.Bit(point.Name, area, address, bitOffset);
                }
                else if (scale is { } wordScale)
                {
                    map.Word(point.Name, area, address, wordScale);
                }
                else
                {
                    map.Word(point.Name, area, address);
                }

                if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
                {
                    map.Writable(point.Name);
                }
            }
        };

    private static HostLinkArea ParseArea(string? value)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "cio" => HostLinkArea.Cio,
            "lr" => HostLinkArea.Link,
            "hr" => HostLinkArea.Holding,
            "ar" => HostLinkArea.Auxiliary,
            "dm" => HostLinkArea.DataMemory,
            _ => throw new ZeusException($"Host Link area「{value}」不受支持。")
        };
}
