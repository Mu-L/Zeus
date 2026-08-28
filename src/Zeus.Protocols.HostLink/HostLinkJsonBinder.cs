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

            ParseArea(ZeusConfigurationOptions.GetString(point.Options, "area", path: pointPath));
            ReadAddress(point, pointPath);
            ValidateBitUsage(point, ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(point.Options, "dataType", "word", pointPath)), pointPath);
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
                ValidateBitUsage(point, dataType, $"point {point.Name}");
                var address = ReadAddress(point, $"point {point.Name}");
                var bitOffset = ReadBitOffset(point, $"point {point.Name}");
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

    private static ushort ReadAddress(PointConfiguration point, string path)
    {
        var address = ZeusConfigurationOptions.GetInt32(point.Options, "address", path: path);
        if (address is < 0 or > 9999)
        {
            throw new ZeusException($"{path}.options.address 必须介于 0 与 9999 之间。");
        }

        return (ushort)address;
    }

    private static byte ReadBitOffset(PointConfiguration point, string path)
    {
        var bitOffset = ZeusConfigurationOptions.GetInt32(point.Options, "bit", path: path);
        if (bitOffset is < 0 or > 15)
        {
            throw new ZeusException($"{path}.options.bit 必须介于 0 与 15 之间。");
        }

        return (byte)bitOffset;
    }

    private static void ValidateBitUsage(PointConfiguration point, string dataType, string path)
    {
        if (dataType is "bit")
        {
            ReadBitOffset(point, path);
            return;
        }

        if (ZeusConfigurationOptions.GetInt32(point.Options, "bit", path: path) != 0)
        {
            throw new ZeusException($"{path}.options.bit 只能用于 Host Link bit 点。");
        }
    }
}
