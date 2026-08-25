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
                var address = ZeusConfigurationOptions.GetInt32(point.Options, "address");
                var bitOffset = (byte)ZeusConfigurationOptions.GetInt32(point.Options, "bit");
                var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale");
                if (area is "x" or "y" or "r" or "l")
                {
                    var contact = area switch
                    {
                        "x" => MewtocolContactArea.ExternalInput,
                        "y" => MewtocolContactArea.ExternalOutput,
                        "l" => MewtocolContactArea.LinkRelay,
                        _ => MewtocolContactArea.InternalRelay
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
                        "ld" => MewtocolDataArea.LinkDataRegister,
                        "fl" => MewtocolDataArea.FileRegister,
                        _ => MewtocolDataArea.DataRegister
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
}
