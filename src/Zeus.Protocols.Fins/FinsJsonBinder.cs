namespace Zeus;

/// <summary>Omron FINS 的 JSON 设备与虚拟从站绑定。由配置核心探测本程序集后登记。</summary>
public sealed class FinsJsonBinder : IZeusJsonBinder
{
    /// <inheritdoc />
    public IReadOnlyList<string> DeviceTypes { get; } = ["omron-fins"];

    /// <inheritdoc />
    public IReadOnlyList<string> ResponderTypes { get; } = ["fins"];

    /// <inheritdoc />
    public void ValidateDevice(DeviceConfiguration device, string path)
    {
        if (ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds", path) is <= 0)
        {
            throw new ZeusException($"{path}.options.timeoutMilliseconds 必须大于 0。");
        }

        ParseWordOrder(ZeusConfigurationOptions.GetString(device.Options, "wordOrder", "high-word-first", path), $"{path}.options.wordOrder");
        ParseTransport(ZeusConfigurationOptions.GetString(device.Options, "transport", "udp", path), $"{path}.options.transport");
        ValidateByteOptions(device, path);
        ValidatePoints(device.Points, path);
    }

    /// <inheritdoc />
    public void ValidateResponder(ChannelConfiguration channel, string path)
    {
        var transportValue = ZeusConfigurationOptions.GetString(channel.Options, "transport", "udp", path);
        var transport = ZeusConfigurationText.Normalize(transportValue);
        if (transport is not ("udp" or "tcp"))
        {
            throw new ZeusException($"{path}.options.transport「{transportValue}」不受支持。FINS 虚拟从站可选 udp、tcp。");
        }
    }

    /// <inheritdoc />
    public void ApplyDevice(DeviceConfiguration device, ZeusHostBuilder? builder = null, IZeusHost? host = null)
    {
        if (builder is not null)
        {
            builder.AddOmronFins(device.Name.Trim(), device.Channel.Trim(), ParseTransport(GetString(device, "transport", "udp"), "device.options.transport"), Options(device), Timeout(device), Points(device));
            return;
        }

        host!.AddOmronFins(device.Name.Trim(), device.Channel.Trim(), ParseTransport(GetString(device, "transport", "udp"), "device.options.transport"), Options(device), Timeout(device), Points(device));
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "responder")) == "fins"
            ? new FinsSlaveResponder(ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "transport", "udp")) == "tcp" ? FinsTransport.Tcp : FinsTransport.Udp)
            : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => ZeusConfigurationOptions.Fingerprint(device.Options);

    private static FinsTransport ParseTransport(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "" or "udp" => FinsTransport.Udp,
            "tcp" => FinsTransport.Tcp,
            _ => throw new ZeusException($"{path}「{value}」不受支持。FINS 设备可选 udp、tcp。")
        };

    private static TimeSpan? Timeout(DeviceConfiguration device)
        => ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds") is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static Action<FinsPointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map => ApplyPoints(map, device.Points);

    private static FinsOptions Options(DeviceConfiguration device)
        => new()
        {
            DestinationNetwork = ReadByte(device, "destinationNetwork"),
            DestinationNode = ReadByte(device, "destinationNode"),
            DestinationUnit = ReadByte(device, "destinationUnit"),
            SourceNetwork = ReadByte(device, "sourceNetwork"),
            SourceNode = ReadByte(device, "sourceNode"),
            SourceUnit = ReadByte(device, "sourceUnit"),
            GatewayCount = ReadByte(device, "gatewayCount", 2),
            InformationControlField = ReadByte(device, "informationControlField", 0x80),
            TcpRequestedClientNode = ReadByte(device, "tcpRequestedClientNode"),
            UseTcpNodeAddressHandshake = ZeusConfigurationOptions.GetBoolean(device.Options, "useTcpNodeAddressHandshake", true),
            WordOrder = ParseWordOrder(GetString(device, "wordOrder", "high-word-first"), "device.options.wordOrder")
        };

    private static void ValidatePoints(List<PointConfiguration> points, string devicePath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            ZeusConfigurationText.EnsureName(point.Name, $"{devicePath}.points");
            if (!names.Add(point.Name.Trim()))
            {
                throw new ZeusException($"{devicePath} 点名 {point.Name} 重复。");
            }

            var path = $"{devicePath}.points[{i}]";
            var areaValue = ZeusConfigurationOptions.GetString(point.Options, "area", path: path);
            if (string.IsNullOrWhiteSpace(areaValue))
            {
                throw new ZeusException($"{path}.options.area 必须指定。");
            }

            var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "word", path), $"{path}.options.dataType");
            ParseArea(areaValue, dataType, $"{path}.options.area");
            ReadAddress(point, path);
            ValidateBitUsage(point, dataType, path);
            ZeusConfigurationText.ValidatePointAlarms(point, path);
        }
    }

    private static void ApplyPoints(FinsPointMap map, List<PointConfiguration> points)
    {
        foreach (var point in points)
        {
            var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "word"), $"point {point.Name}.options.dataType");
            var area = ParseArea(ZeusConfigurationOptions.GetString(point.Options, "area"), dataType, $"point {point.Name}.options.area");
            var alarmLimits = ZeusConfigurationText.CreateAlarmLimits(point);
            var address = ReadAddress(point, $"point {point.Name}");
            var bitOffset = ReadBitOffset(point, $"point {point.Name}");
            var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale");
            if (dataType == FinsDataType.Bit)
            {
                map.Bit(point.Name, area, address, bitOffset);
            }
            else if (scale is { } wordScale)
            {
                map.Word(point.Name, area, address, wordScale);
                if (alarmLimits is not null)
                {
                    map.WithAlarmLimits(point.Name, alarmLimits.Low, alarmLimits.High);
                }
            }
            else
            {
                map.Word(point.Name, area, address);
                if (alarmLimits is not null)
                {
                    map.WithAlarmLimits(point.Name, alarmLimits.Low, alarmLimits.High);
                }
            }

            if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
            {
                map.Writable(point.Name);
            }
        }
    }

    private static string? GetString(DeviceConfiguration device, string name, string? defaultValue = null)
        => ZeusConfigurationOptions.GetString(device.Options, name, defaultValue);

    private static int GetInt32(DeviceConfiguration device, string name, int defaultValue = 0)
        => ZeusConfigurationOptions.GetInt32(device.Options, name, defaultValue);

    private static void ValidateByteOptions(DeviceConfiguration device, string path)
    {
        ReadByte(device, "destinationNetwork", path: path);
        ReadByte(device, "destinationNode", path: path);
        ReadByte(device, "destinationUnit", path: path);
        ReadByte(device, "sourceNetwork", path: path);
        ReadByte(device, "sourceNode", path: path);
        ReadByte(device, "sourceUnit", path: path);
        ReadByte(device, "gatewayCount", 2, path);
        ReadByte(device, "informationControlField", 0x80, path);
        ReadByte(device, "tcpRequestedClientNode", path: path);
    }

    private static byte ReadByte(DeviceConfiguration device, string name, int defaultValue = 0, string? path = null)
    {
        var value = ZeusConfigurationOptions.GetInt32(device.Options, name, defaultValue, path);
        if (value is < byte.MinValue or > byte.MaxValue)
        {
            var label = string.IsNullOrWhiteSpace(path) ? name : $"{path}.options.{name}";
            throw new ZeusException($"{label} 必须介于 0 与 255 之间。");
        }

        return (byte)value;
    }

    private static ushort ReadAddress(PointConfiguration point, string path)
    {
        var address = ZeusConfigurationOptions.GetInt32(point.Options, "address", path: path);
        if (address is < 0 or > ushort.MaxValue)
        {
            throw new ZeusException($"{path}.options.address 必须介于 0 与 65535 之间。");
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

    private static void ValidateBitUsage(PointConfiguration point, FinsDataType dataType, string path)
    {
        if (dataType == FinsDataType.Bit)
        {
            ReadBitOffset(point, path);
            return;
        }

        if (ZeusConfigurationOptions.GetInt32(point.Options, "bit", path: path) != 0)
        {
            throw new ZeusException($"{path}.options.bit 只能用于 FINS bit 点。");
        }
    }

    private static FinsDataType ParseDataType(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "" or "word" => FinsDataType.Word,
            "bit" => FinsDataType.Bit,
            "int16" => FinsDataType.Int16,
            "uint32" => FinsDataType.UInt32,
            "int32" => FinsDataType.Int32,
            "real" => FinsDataType.Real,
            _ => throw new ZeusException($"{path}「{value}」不受支持。")
        };

    private static FinsMemoryAreaCode ParseArea(string? value, FinsDataType dataType, string path)
    {
        var token = ZeusConfigurationText.Normalize(value);
        var bit = dataType == FinsDataType.Bit;
        return token switch
        {
            "cio" => bit ? FinsMemoryAreaCode.CioBit : FinsMemoryAreaCode.CioWord,
            "wr" => bit ? FinsMemoryAreaCode.WorkBit : FinsMemoryAreaCode.WorkWord,
            "hr" => bit ? FinsMemoryAreaCode.HoldingBit : FinsMemoryAreaCode.HoldingWord,
            "ar" => bit ? FinsMemoryAreaCode.AuxiliaryBit : FinsMemoryAreaCode.AuxiliaryWord,
            "dm" => bit ? FinsMemoryAreaCode.DataMemoryBit : FinsMemoryAreaCode.DataMemoryWord,
            "tc" => bit ? FinsMemoryAreaCode.TimerCounterFlag : FinsMemoryAreaCode.TimerCounterValue,
            "em" => bit ? FinsMemoryAreaCode.CurrentEmBit : FinsMemoryAreaCode.CurrentEmWord,
            _ => throw new ZeusException($"{path}「{value}」不受支持。")
        };
    }

    private static FinsWordOrder ParseWordOrder(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "" or "high-word-first" => FinsWordOrder.HighWordFirst,
            "low-word-first" => FinsWordOrder.LowWordFirst,
            _ => throw new ZeusException($"{path}「{value}」不受支持。")
        };
}
