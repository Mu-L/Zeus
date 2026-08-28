namespace Zeus;

/// <summary>Panasonic MEWTOCOL 的 JSON 设备与虚拟从站绑定。由配置核心探测本程序集后登记。</summary>
public sealed class MewtocolJsonBinder : IZeusJsonBinder
{
    /// <inheritdoc />
    public IReadOnlyList<string> DeviceTypes { get; } = ["panasonic-mewtocol"];

    /// <inheritdoc />
    public IReadOnlyList<string> ResponderTypes { get; } = ["mewtocol"];

    /// <inheritdoc />
    public void ValidateDevice(DeviceConfiguration device, string path)
    {
        var unitId = ZeusConfigurationOptions.GetInt32(device.Options, "unitId", 1, path);
        if (unitId is < 1 or > 99)
        {
            throw new ZeusException($"{path}.options.unitId 必须介于 1 与 99 之间。");
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
            var area = ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(point.Options, "area", "dt", pointPath));
            ValidateArea(area, pointPath);
            ReadAddress(point, area, pointPath);
            ValidateBitUsage(point, ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(point.Options, "dataType", "word", pointPath)), pointPath);
        }
    }

    /// <inheritdoc />
    public void ValidateResponder(ChannelConfiguration channel, string path)
    {
        var unitId = ZeusConfigurationOptions.GetInt32(channel.Options, "unitId", 1, path);
        if (unitId is < 1 or > 99)
        {
            throw new ZeusException($"{path}.options.unitId 必须介于 1 与 99 之间。");
        }
    }

    /// <inheritdoc />
    public void ApplyDevice(DeviceConfiguration device, ZeusHostBuilder? builder = null, IZeusHost? host = null)
    {
        if (builder is not null)
        {
            builder.AddPanasonicMewtocol(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
            return;
        }

        host!.AddPanasonicMewtocol(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "responder")) == "mewtocol"
            ? new MewtocolSlaveResponder((byte)ZeusConfigurationOptions.GetInt32(channel.Options, "unitId", 1))
            : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => ZeusConfigurationOptions.Fingerprint(device.Options);

    private static TimeSpan? Timeout(DeviceConfiguration device)
        => ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds") is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static MewtocolOptions Options(DeviceConfiguration device)
        => new() { StationNumber = (byte)ZeusConfigurationOptions.GetInt32(device.Options, "unitId", 1) };

    private static Action<MewtocolPointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var area = ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(point.Options, "area", "dt"));
                var dataType = ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(point.Options, "dataType", "word"));
                ValidateArea(area, $"point {point.Name}");
                ValidateBitUsage(point, dataType, $"point {point.Name}");
                var address = ReadAddress(point, area, $"point {point.Name}");
                var bitOffset = ReadBitOffset(point, $"point {point.Name}");
                var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale");
                if (area is "x" or "y" or "r" or "l")
                {
                    var contact = area switch
                    {
                        "x" => MewtocolContactArea.ExternalInput,
                        "y" => MewtocolContactArea.ExternalOutput,
                        "l" => MewtocolContactArea.LinkRelay,
                        "r" => MewtocolContactArea.InternalRelay,
                        _ => throw new ZeusException($"MEWTOCOL area「{area}」不受支持。")
                    };
                    if (dataType is "bit")
                    {
                        map.Bit(point.Name, contact, address, bitOffset);
                    }
                    else if (scale is { } wordScale)
                    {
                        map.Word(point.Name, contact, address, wordScale);
                    }
                    else
                    {
                        map.Word(point.Name, contact, address);
                    }
                }
                else
                {
                    var data = area switch
                    {
                        "dt" => MewtocolDataArea.DataRegister,
                        "ld" => MewtocolDataArea.LinkDataRegister,
                        "fl" => MewtocolDataArea.FileRegister,
                        _ => throw new ZeusException($"MEWTOCOL area「{area}」不受支持。")
                    };
                    if (dataType is "bit")
                    {
                        map.Bit(point.Name, data, address, bitOffset);
                    }
                    else if (scale is { } dataScale)
                    {
                        map.Word(point.Name, data, address, dataScale);
                    }
                    else
                    {
                        map.Word(point.Name, data, address);
                    }
                }

                if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
                {
                    map.Writable(point.Name);
                }
            }
        };

    private static void ValidateArea(string area, string path)
    {
        if (area is not ("dt" or "ld" or "fl" or "x" or "y" or "r" or "l"))
        {
            throw new ZeusException($"{path}.options.area「{area}」不受支持。MEWTOCOL 可选 dt、ld、fl、x、y、r、l。");
        }
    }

    private static int ReadAddress(PointConfiguration point, string area, string path)
    {
        var address = ZeusConfigurationOptions.GetInt32(point.Options, "address", path: path);
        if (area is "x" or "y" or "r" or "l")
        {
            if (address is < 0 or > 9999)
            {
                throw new ZeusException($"{path}.options.address 必须介于 0 与 9999 之间。");
            }

            return address;
        }

        if (address is < 0 or > 99999)
        {
            throw new ZeusException($"{path}.options.address 必须介于 0 与 99999 之间。");
        }

        return address;
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
            throw new ZeusException($"{path}.options.bit 只能用于 MEWTOCOL bit 点。");
        }
    }
}
