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
            DestinationNetwork = (byte)GetInt32(device, "destinationNetwork"),
            DestinationNode = (byte)GetInt32(device, "destinationNode"),
            DestinationUnit = (byte)GetInt32(device, "destinationUnit"),
            SourceNetwork = (byte)GetInt32(device, "sourceNetwork"),
            SourceNode = (byte)GetInt32(device, "sourceNode"),
            SourceUnit = (byte)GetInt32(device, "sourceUnit"),
            GatewayCount = (byte)GetInt32(device, "gatewayCount", 2),
            InformationControlField = (byte)GetInt32(device, "informationControlField", 0x80),
            TcpRequestedClientNode = (byte)GetInt32(device, "tcpRequestedClientNode"),
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
            var address = (ushort)ZeusConfigurationOptions.GetInt32(point.Options, "address");
            var bitOffset = (byte)ZeusConfigurationOptions.GetInt32(point.Options, "bit");
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
