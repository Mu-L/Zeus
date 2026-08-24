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
        if (device.TimeoutMilliseconds is <= 0)
        {
            throw new ZeusException($"{path}.timeoutMilliseconds 必须大于 0。");
        }

        ParseWordOrder(device.WordOrder, $"{path}.wordOrder");
        ParseTransport(device.Transport, $"{path}.transport");
        ValidatePoints(device.Points, path);
    }

    /// <inheritdoc />
    public void ValidateResponder(ChannelConfiguration channel, string path)
    {
        var transport = ZeusConfigurationText.Normalize(channel.Transport);
        if (transport is not ("udp" or "tcp"))
        {
            throw new ZeusException($"{path}.transport「{channel.Transport}」不受支持。FINS 虚拟从站可选 udp、tcp。");
        }
    }

    /// <inheritdoc />
    public void ApplyDevice(DeviceConfiguration device, ZeusHostBuilder? builder = null, IZeusHost? host = null)
    {
        if (builder is not null)
        {
            builder.AddOmronFins(device.Name.Trim(), device.Channel.Trim(), ParseTransport(device.Transport, "device.transport"), Options(device), Timeout(device), Points(device));
            return;
        }

        host!.AddOmronFins(device.Name.Trim(), device.Channel.Trim(), ParseTransport(device.Transport, "device.transport"), Options(device), Timeout(device), Points(device));
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(channel.Responder) == "fins"
            ? new FinsSlaveResponder(ZeusConfigurationText.Normalize(channel.Transport) == "tcp" ? FinsTransport.Tcp : FinsTransport.Udp)
            : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => string.Join('|', ZeusConfigurationText.Normalize(device.Transport), device.TimeoutMilliseconds, device.DestinationNode, device.SourceNode, ZeusConfigurationText.Normalize(device.WordOrder));

    private static FinsTransport ParseTransport(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "" or "udp" => FinsTransport.Udp,
            "tcp" => FinsTransport.Tcp,
            _ => throw new ZeusException($"{path}「{value}」不受支持。FINS 设备可选 udp、tcp。")
        };

    private static TimeSpan? Timeout(DeviceConfiguration device)
        => device.TimeoutMilliseconds is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static Action<FinsPointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map => ApplyPoints(map, device.Points);

    private static FinsOptions Options(DeviceConfiguration device)
        => new()
        {
            DestinationNetwork = (byte)device.DestinationNetwork,
            DestinationNode = (byte)device.DestinationNode,
            DestinationUnit = (byte)device.DestinationUnit,
            SourceNetwork = (byte)device.SourceNetwork,
            SourceNode = (byte)device.SourceNode,
            SourceUnit = (byte)device.SourceUnit,
            GatewayCount = (byte)device.GatewayCount,
            InformationControlField = (byte)device.InformationControlField,
            TcpRequestedClientNode = (byte)device.TcpRequestedClientNode,
            UseTcpNodeAddressHandshake = device.UseTcpNodeAddressHandshake,
            WordOrder = ParseWordOrder(device.WordOrder, "device.wordOrder")
        };

    private static void ValidatePoints(List<PointConfiguration> points, string devicePath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var point in points)
        {
            ZeusConfigurationText.EnsureName(point.Name, $"{devicePath}.points");
            if (!names.Add(point.Name.Trim()))
            {
                throw new ZeusException($"{devicePath} 点名 {point.Name} 重复。");
            }

            if (string.IsNullOrWhiteSpace(point.Area))
            {
                throw new ZeusException($"点 {point.Name}.area 必须指定。");
            }

            var dataType = ParseDataType(point.DataType, $"point {point.Name}.dataType");
            ParseArea(point.Area, dataType, $"point {point.Name}.area");
            ZeusConfigurationText.ValidatePointAlarms(point, $"point {point.Name}");
        }
    }

    private static void ApplyPoints(FinsPointMap map, List<PointConfiguration> points)
    {
        foreach (var point in points)
        {
            var dataType = ParseDataType(point.DataType, $"point {point.Name}.dataType");
            var area = ParseArea(point.Area, dataType, $"point {point.Name}.area");
            var alarmLimits = ZeusConfigurationText.CreateAlarmLimits(point);
            if (dataType == FinsDataType.Bit)
            {
                map.Bit(point.Name, area, (ushort)point.Address, (byte)point.BitOffset);
            }
            else if (point.Scale is { } scale)
            {
                map.Word(point.Name, area, (ushort)point.Address, scale);
                if (alarmLimits is not null)
                {
                    map.WithAlarmLimits(point.Name, alarmLimits.Low, alarmLimits.High);
                }
            }
            else
            {
                map.Word(point.Name, area, (ushort)point.Address);
                if (alarmLimits is not null)
                {
                    map.WithAlarmLimits(point.Name, alarmLimits.Low, alarmLimits.High);
                }
            }

            if (point.Writable)
            {
                map.Writable(point.Name);
            }
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
