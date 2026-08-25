namespace Zeus;

/// <summary>
/// Mitsubishi MC 的 JSON 设备与虚拟从站绑定。由配置核心探测本程序集后登记。
/// </summary>
public sealed class McJsonBinder : IZeusJsonBinder
{
    /// <inheritdoc />
    public IReadOnlyList<string> DeviceTypes { get; } = ["mitsubishi-mc"];

    /// <inheritdoc />
    public IReadOnlyList<string> ResponderTypes { get; } = ["mc"];

    /// <inheritdoc />
    public void ValidateDevice(DeviceConfiguration device, string path)
    {
        var frameType = ParseFrameType(GetString(device, "frameType", "3e"), $"{path}.options.frameType");
        ParseEncoding(GetString(device, "encoding", "binary"), $"{path}.options.encoding");
        ValidateByte(GetInt32(device, "networkNumber"), $"{path}.options.networkNumber");
        ValidateByte(GetInt32(device, "pcNumber", 0xFF), $"{path}.options.pcNumber");
        ValidateUInt16(GetInt32(device, "ioNumber", 0x03FF), $"{path}.options.ioNumber");
        ValidateByte(GetInt32(device, "stationNumber"), $"{path}.options.stationNumber");
        ValidateUInt16(GetInt32(device, "monitoringTimer", 0x0010), $"{path}.options.monitoringTimer");
        ValidateUInt16(GetInt32(device, "serialNumber"), $"{path}.options.serialNumber");
        if (GetNullableInt32(device, "timeoutMilliseconds", path) is <= 0)
        {
            throw new ZeusException($"{path}.options.timeoutMilliseconds 必须大于 0。");
        }

        ValidatePoints(device.Points, path, frameType);
    }

    /// <inheritdoc />
    public void ValidateResponder(ChannelConfiguration channel, string path)
    {
    }

    /// <inheritdoc />
    public void ApplyDevice(DeviceConfiguration device, ZeusHostBuilder? builder = null, IZeusHost? host = null)
    {
        var timeout = ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds") is { } ms ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null;
        Action<McPointMap>? points = device.Points.Count == 0 ? null : map => ApplyPoints(map, device.Points);
        if (builder is not null)
        {
            builder.AddMitsubishiMc(device.Name.Trim(), device.Channel.Trim(), CreateOptions(device), timeout, points);
            return;
        }

        host!.AddMitsubishiMc(device.Name.Trim(), device.Channel.Trim(), CreateOptions(device), timeout, points);
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "responder")) == "mc" ? new McSlaveResponder() : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => ZeusConfigurationOptions.Fingerprint(device.Options);

    private static McOptions CreateOptions(DeviceConfiguration device)
        => new()
        {
            FrameType = ParseFrameType(GetString(device, "frameType", "3e"), "device.options.frameType"),
            DataEncoding = ParseEncoding(GetString(device, "encoding", "binary"), "device.options.encoding"),
            NetworkNumber = (byte)GetInt32(device, "networkNumber"),
            PcNumber = (byte)GetInt32(device, "pcNumber", 0xFF),
            IoNumber = (ushort)GetInt32(device, "ioNumber", 0x03FF),
            StationNumber = (byte)GetInt32(device, "stationNumber"),
            MonitoringTimer = (ushort)GetInt32(device, "monitoringTimer", 0x0010),
            SerialNumber = (ushort)GetInt32(device, "serialNumber")
        };

    private static void ValidatePoints(List<PointConfiguration> points, string devicePath, McFrameType frameType)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var path = $"{devicePath}.points[{i}]";
            ZeusConfigurationText.EnsureName(point.Name, path);
            if (!names.Add(point.Name.Trim()))
            {
                throw new ZeusException($"{path}.name「{point.Name}」在同一设备内重复。");
            }

            var deviceCodeValue = ZeusConfigurationOptions.GetString(point.Options, "deviceCode", path: path);
            if (string.IsNullOrWhiteSpace(deviceCodeValue))
            {
                throw new ZeusException($"{path}.options.deviceCode 必须指定。Mitsubishi MC 可选 D、M、X、Y、W、R、ZR。");
            }

            var deviceCode = ParseDeviceCode(deviceCodeValue, $"{path}.options.deviceCode");
            if (frameType == McFrameType.Frame1E && deviceCode == McDeviceCode.ExtendedFileRegister)
            {
                throw new ZeusException($"{path}.options.deviceCode 为 ZR，但 MC 1E 帧不支持 ZR。请改用 3e/4e，或移除该点。");
            }

            var address = ZeusConfigurationOptions.GetInt32(point.Options, "address", path: path);
            if (address is < 0 or > 0xFFFFFF)
            {
                throw new ZeusException($"{path}.options.address 必须介于 0 与 16777215 之间。");
            }

            var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale", path);
            if (scale is <= 0)
            {
                throw new ZeusException($"{path}.options.scale 必须大于 0。");
            }

            ZeusConfigurationText.ValidatePointAlarms(point, path);
            var isBit = deviceCode is McDeviceCode.InternalRelay or McDeviceCode.InputRelay or McDeviceCode.OutputRelay;
            if (scale is not null && isBit)
            {
                throw new ZeusException($"{path} 是 MC 位软元件，不能配置 options.scale。");
            }

            if ((ZeusConfigurationOptions.GetNullableDouble(point.Options, "lowAlarmLimit", path) is not null
                    || ZeusConfigurationOptions.GetNullableDouble(point.Options, "highAlarmLimit", path) is not null) && isBit)
            {
                throw new ZeusException($"{path} 是 MC 位软元件，不能配置 options.lowAlarmLimit 或 options.highAlarmLimit。");
            }

            if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable", path: path) && deviceCode == McDeviceCode.InputRelay)
            {
                throw new ZeusException($"{path}.options.deviceCode 为 X 输入继电器，该软元件只读，不能设置 options.writable: true。");
            }
        }
    }

    private static void ApplyPoints(McPointMap map, List<PointConfiguration> points)
    {
        foreach (var point in points)
        {
            var deviceCode = ParseDeviceCode(ZeusConfigurationOptions.GetString(point.Options, "deviceCode"), $"point {point.Name}.options.deviceCode");
            var alarmLimits = ZeusConfigurationText.CreateAlarmLimits(point);
            var address = ZeusConfigurationOptions.GetInt32(point.Options, "address");
            var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale");
            var isWord = deviceCode is McDeviceCode.DataRegister
                or McDeviceCode.LinkRegister
                or McDeviceCode.FileRegister
                or McDeviceCode.ExtendedFileRegister;
            if (isWord)
            {
                if (scale is { } wordScale)
                {
                    map.Word(point.Name, deviceCode, address, wordScale);
                }
                else
                {
                    map.Word(point.Name, deviceCode, address);
                }

                if (alarmLimits is not null)
                {
                    map.WithAlarmLimits(point.Name, alarmLimits.Low, alarmLimits.High);
                }
            }
            else
            {
                map.Bit(point.Name, deviceCode, address);
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

    private static int? GetNullableInt32(DeviceConfiguration device, string name, string path)
        => ZeusConfigurationOptions.GetNullableInt32(device.Options, name, path);

    private static McFrameType ParseFrameType(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "1e" => McFrameType.Frame1E,
            "3e" or "" => McFrameType.Frame3E,
            "4e" => McFrameType.Frame4E,
            _ => throw new ZeusException($"{path}「{value}」不受支持。可选 1e、3e、4e。")
        };

    private static McDataEncoding ParseEncoding(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "binary" or "" => McDataEncoding.Binary,
            "ascii" => McDataEncoding.Ascii,
            _ => throw new ZeusException($"{path}「{value}」不受支持。可选 binary、ascii。")
        };

    private static McDeviceCode ParseDeviceCode(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "d" => McDeviceCode.DataRegister,
            "m" => McDeviceCode.InternalRelay,
            "x" => McDeviceCode.InputRelay,
            "y" => McDeviceCode.OutputRelay,
            "w" => McDeviceCode.LinkRegister,
            "r" => McDeviceCode.FileRegister,
            "zr" => McDeviceCode.ExtendedFileRegister,
            _ => throw new ZeusException($"{path}「{value}」不受支持。可选 D、M、X、Y、W、R、ZR。")
        };

    private static void ValidateByte(int value, string path)
    {
        if (value is < 0 or > byte.MaxValue)
        {
            throw new ZeusException($"{path} 必须介于 0 与 255 之间。");
        }
    }

    private static void ValidateUInt16(int value, string path)
    {
        if (value is < 0 or > ushort.MaxValue)
        {
            throw new ZeusException($"{path} 必须介于 0 与 65535 之间。");
        }
    }
}
