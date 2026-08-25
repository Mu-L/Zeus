namespace Zeus;

/// <summary>
/// Siemens S7 的 JSON 设备与虚拟从站绑定。由配置核心探测本程序集后登记。
/// </summary>
public sealed class S7JsonBinder : IZeusJsonBinder
{
    /// <inheritdoc />
    public IReadOnlyList<string> DeviceTypes { get; } = ["siemens-s7"];

    /// <inheritdoc />
    public IReadOnlyList<string> ResponderTypes { get; } = ["s7"];

    /// <inheritdoc />
    public void ValidateDevice(DeviceConfiguration device, string path)
    {
        ValidateByte(GetInt32(device, "rack"), $"{path}.options.rack");
        if (GetInt32(device, "slot", 1) is < 0 or > 31)
        {
            throw new ZeusException($"{path}.options.slot 必须介于 0 与 31 之间。");
        }

        ValidateUInt16(GetInt32(device, "localTsap", 0x0100), $"{path}.options.localTsap");
        if (GetNullableInt32(device, "remoteTsap", path) is { } remoteTsap)
        {
            ValidateUInt16(remoteTsap, $"{path}.options.remoteTsap");
        }

        if (GetInt32(device, "requestedPduLength", 480) is < 128 or > 960)
        {
            throw new ZeusException($"{path}.options.requestedPduLength 必须介于 128 与 960 之间。");
        }

        if (GetNullableInt32(device, "timeoutMilliseconds", path) is <= 0)
        {
            throw new ZeusException($"{path}.options.timeoutMilliseconds 必须大于 0。");
        }

        ValidatePoints(device.Points, path);
    }

    /// <inheritdoc />
    public void ValidateResponder(ChannelConfiguration channel, string path)
    {
    }

    /// <inheritdoc />
    public void ApplyDevice(DeviceConfiguration device, ZeusHostBuilder? builder = null, IZeusHost? host = null)
    {
        var timeout = ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds") is { } ms ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null;
        Action<S7PointMap>? points = device.Points.Count == 0 ? null : map => ApplyPoints(map, device.Points);
        if (builder is not null)
        {
            builder.AddSiemensS7(device.Name.Trim(), device.Channel.Trim(), CreateOptions(device), timeout, points);
            return;
        }

        host!.AddSiemensS7(device.Name.Trim(), device.Channel.Trim(), CreateOptions(device), timeout, points);
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "responder")) == "s7" ? new S7SlaveResponder() : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => ZeusConfigurationOptions.Fingerprint(device.Options);

    private static S7Options CreateOptions(DeviceConfiguration device)
        => new()
        {
            Rack = (byte)GetInt32(device, "rack"),
            Slot = (byte)GetInt32(device, "slot", 1),
            LocalTsap = (ushort)GetInt32(device, "localTsap", 0x0100),
            RemoteTsap = GetNullableInt32(device, "remoteTsap") is { } remoteTsap ? (ushort)remoteTsap : null,
            RequestedPduLength = (ushort)GetInt32(device, "requestedPduLength", 480)
        };

    private static void ValidatePoints(List<PointConfiguration> points, string devicePath)
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

            var areaValue = ZeusConfigurationOptions.GetString(point.Options, "area", path: path);
            if (string.IsNullOrWhiteSpace(areaValue))
            {
                throw new ZeusException($"{path}.options.area 必须指定。S7 可选 db、m、i、q。");
            }

            var area = ParseArea(areaValue, $"{path}.options.area");
            var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "word", path), $"{path}.options.dataType");
            var address = ZeusConfigurationOptions.GetInt32(point.Options, "address", path: path);
            if (address is < 0 or > 0x1FFFFF)
            {
                throw new ZeusException($"{path}.options.address 必须介于 0 与 2097151 之间。");
            }

            var dbNumber = ZeusConfigurationOptions.GetInt32(point.Options, "db", path: path);
            if (area == S7Area.DataBlock)
            {
                if (dbNumber is <= 0 or > ushort.MaxValue)
                {
                    throw new ZeusException($"{path}.options.db 必须介于 1 与 65535 之间。");
                }
            }
            else if (dbNumber != 0)
            {
                throw new ZeusException($"{path}.options.db 只能用于 S7 DB 区。");
            }

            var bitOffset = ZeusConfigurationOptions.GetInt32(point.Options, "bit", path: path);
            if (dataType == S7DataType.Bool)
            {
                if (bitOffset is < 0 or > 7)
                {
                    throw new ZeusException($"{path}.options.bit 必须介于 0 与 7 之间。");
                }
            }
            else if (bitOffset != 0)
            {
                throw new ZeusException($"{path}.options.bit 只能用于 S7 bool 点。");
            }

            var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale", path);
            if (scale is <= 0)
            {
                throw new ZeusException($"{path}.options.scale 必须大于 0。");
            }

            ZeusConfigurationText.ValidatePointAlarms(point, path);
            if (dataType == S7DataType.Bool)
            {
                if (scale is not null)
                {
                    throw new ZeusException($"{path} 是 S7 bool 点，不能配置 options.scale。");
                }

                if (ZeusConfigurationOptions.GetNullableDouble(point.Options, "lowAlarmLimit", path) is not null
                    || ZeusConfigurationOptions.GetNullableDouble(point.Options, "highAlarmLimit", path) is not null)
                {
                    throw new ZeusException($"{path} 是 S7 bool 点，不能配置 options.lowAlarmLimit 或 options.highAlarmLimit。");
                }
            }

            if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable", path: path) && area == S7Area.Inputs)
            {
                throw new ZeusException($"{path}.options.area 为 I 输入区，该区域只读，不能设置 options.writable: true。");
            }
        }
    }

    private static void ApplyPoints(S7PointMap map, List<PointConfiguration> points)
    {
        foreach (var point in points)
        {
            var area = ParseArea(ZeusConfigurationOptions.GetString(point.Options, "area"), $"point {point.Name}.options.area");
            var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "word"), $"point {point.Name}.options.dataType");
            var alarmLimits = ZeusConfigurationText.CreateAlarmLimits(point);
            var address = ZeusConfigurationOptions.GetInt32(point.Options, "address");
            var dbNumber = ZeusConfigurationOptions.GetInt32(point.Options, "db");
            var bitOffset = ZeusConfigurationOptions.GetInt32(point.Options, "bit");
            if (ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale") is { } scale)
            {
                map.ScaledPoint(point.Name, area, dataType, address, scale, dbNumber, bitOffset, alarmLimits);
            }
            else
            {
                map.Point(point.Name, area, dataType, address, dbNumber, bitOffset, alarmLimits);
            }

            if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
            {
                map.Writable(point.Name);
            }
        }
    }

    private static int GetInt32(DeviceConfiguration device, string name, int defaultValue = 0)
        => ZeusConfigurationOptions.GetInt32(device.Options, name, defaultValue);

    private static int? GetNullableInt32(DeviceConfiguration device, string name, string? path = null)
        => ZeusConfigurationOptions.GetNullableInt32(device.Options, name, path);

    private static S7Area ParseArea(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "db" => S7Area.DataBlock,
            "m" => S7Area.Merkers,
            "i" => S7Area.Inputs,
            "q" => S7Area.Outputs,
            _ => throw new ZeusException($"{path}「{value}」不受支持。可选 db、m、i、q。")
        };

    private static S7DataType ParseDataType(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "bool" => S7DataType.Bool,
            "byte" => S7DataType.Byte,
            "word" => S7DataType.Word,
            "dword" => S7DataType.DWord,
            "int" => S7DataType.Int,
            "dint" => S7DataType.DInt,
            "real" => S7DataType.Real,
            _ => throw new ZeusException($"{path}「{value}」不受支持。可选 bool、byte、word、dword、int、dint、real。")
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
