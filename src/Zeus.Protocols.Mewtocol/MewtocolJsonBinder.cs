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

        ParseWordOrder(ZeusConfigurationOptions.GetString(device.Options, "wordOrder", "high-word-first", path), $"{path}.options.wordOrder");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < device.Points.Count; i++)
        {
            var point = device.Points[i];
            var pointPath = $"{path}.points[{i}]";
            ZeusConfigurationText.EnsureName(point.Name, pointPath);
            if (!names.Add(point.Name.Trim()))
            {
                throw new ZeusException($"{path} 点名 {point.Name} 重复。");
            }

            var area = ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(point.Options, "area", "dt", pointPath));
            ValidateArea(area, pointPath);
            ReadAddress(point, area, pointPath);
            var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "word", pointPath), $"{pointPath}.options.dataType");
            ValidateBitUsage(point, dataType, pointPath);
            if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable", path: pointPath) && area == "x")
            {
                throw new ZeusException($"{pointPath}.options.area 为 X 输入区，该区域只读，不能设置 options.writable: true。");
            }
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
        => new()
        {
            StationNumber = (byte)ZeusConfigurationOptions.GetInt32(device.Options, "unitId", 1),
            WordOrder = ParseWordOrder(ZeusConfigurationOptions.GetString(device.Options, "wordOrder", "high-word-first"), "device.options.wordOrder")
        };

    private static Action<MewtocolPointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var area = ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(point.Options, "area", "dt"));
                var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "word"), $"point {point.Name}.options.dataType");
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
                    switch (dataType)
                    {
                        case MewtocolDataType.Bit:
                            map.Bit(point.Name, contact, address, bitOffset);
                            break;
                        case MewtocolDataType.Int16:
                            map.Int16(point.Name, contact, address, scale);
                            break;
                        case MewtocolDataType.UInt32:
                            map.UInt32(point.Name, contact, address, scale);
                            break;
                        case MewtocolDataType.Int32:
                            map.Int32(point.Name, contact, address, scale);
                            break;
                        case MewtocolDataType.Real:
                            map.Real(point.Name, contact, address, scale);
                            break;
                        case MewtocolDataType.Word:
                            if (scale is { } wordScale)
                            {
                                map.Word(point.Name, contact, address, wordScale);
                            }
                            else
                            {
                                map.Word(point.Name, contact, address);
                            }

                            break;
                        default:
                            throw new ZeusException($"不支持的 MEWTOCOL 数据类型：{dataType}。");
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
                    switch (dataType)
                    {
                        case MewtocolDataType.Bit:
                            map.Bit(point.Name, data, address, bitOffset);
                            break;
                        case MewtocolDataType.Int16:
                            map.Int16(point.Name, data, address, scale);
                            break;
                        case MewtocolDataType.UInt32:
                            map.UInt32(point.Name, data, address, scale);
                            break;
                        case MewtocolDataType.Int32:
                            map.Int32(point.Name, data, address, scale);
                            break;
                        case MewtocolDataType.Real:
                            map.Real(point.Name, data, address, scale);
                            break;
                        case MewtocolDataType.Word:
                            if (scale is { } dataScale)
                            {
                                map.Word(point.Name, data, address, dataScale);
                            }
                            else
                            {
                                map.Word(point.Name, data, address);
                            }

                            break;
                        default:
                            throw new ZeusException($"不支持的 MEWTOCOL 数据类型：{dataType}。");
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

    private static MewtocolDataType ParseDataType(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "" or "word" => MewtocolDataType.Word,
            "bit" => MewtocolDataType.Bit,
            "int16" => MewtocolDataType.Int16,
            "uint32" => MewtocolDataType.UInt32,
            "int32" => MewtocolDataType.Int32,
            "real" => MewtocolDataType.Real,
            _ => throw new ZeusException($"{path}「{value}」不受支持。MEWTOCOL 可选 word、bit、int16、uint32、int32、real。")
        };

    private static MewtocolWordOrder ParseWordOrder(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "" or "high-word-first" => MewtocolWordOrder.HighWordFirst,
            "low-word-first" => MewtocolWordOrder.LowWordFirst,
            _ => throw new ZeusException($"{path}「{value}」不受支持。MEWTOCOL wordOrder 可选 high-word-first、low-word-first。")
        };

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

    private static void ValidateBitUsage(PointConfiguration point, MewtocolDataType dataType, string path)
    {
        if (dataType == MewtocolDataType.Bit)
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
