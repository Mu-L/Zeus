namespace Zeus;

/// <summary>
/// Modbus RTU/TCP/ASCII 的 JSON 设备与虚拟从站绑定。由配置核心探测本程序集后登记。
/// </summary>
public sealed class ModbusJsonBinder : IZeusJsonBinder
{
    /// <inheritdoc />
    public IReadOnlyList<string> DeviceTypes { get; } = ["modbus-rtu", "modbus-tcp", "modbus-ascii"];

    /// <inheritdoc />
    public IReadOnlyList<string> ResponderTypes { get; } = ["modbus"];

    /// <inheritdoc />
    public void ValidateDevice(DeviceConfiguration device, string path)
    {
        if (ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds", path) is <= 0)
        {
            throw new ZeusException($"{path}.options.timeoutMilliseconds 必须大于 0。");
        }

        ValidatePoints(device.Points, path);
    }

    /// <inheritdoc />
    public void ValidateResponder(ChannelConfiguration channel, string path)
    {
        var transportValue = ZeusConfigurationOptions.GetString(channel.Options, "transport", "rtu", path);
        var transport = ZeusConfigurationText.Normalize(transportValue);
        if (transport is not ("rtu" or "tcp" or "ascii"))
        {
            throw new ZeusException($"{path}.options.transport「{transportValue}」不受支持。可选 rtu、tcp、ascii。");
        }
    }

    /// <inheritdoc />
    public void ApplyDevice(DeviceConfiguration device, ZeusHostBuilder? builder = null, IZeusHost? host = null)
        => Add(device, (name, channel, unitId, timeout, points) =>
        {
            switch (CreateDeviceTransport(ZeusConfigurationText.Normalize(device.Type), device.Type))
            {
                case ModbusTransport.Tcp:
                    if (builder is not null)
                    {
                        builder.AddModbusTcp(name, channel, unitId, timeout, points);
                    }
                    else
                    {
                        host!.AddModbusTcp(name, channel, unitId, timeout, points);
                    }

                    break;
                case ModbusTransport.Ascii:
                    if (builder is not null)
                    {
                        builder.AddModbusAscii(name, channel, unitId, timeout, points);
                    }
                    else
                    {
                        host!.AddModbusAscii(name, channel, unitId, timeout, points);
                    }

                    break;
                default:
                    if (builder is not null)
                    {
                        builder.AddModbusRtu(name, channel, unitId, timeout, points);
                    }
                    else
                    {
                        host!.AddModbusRtu(name, channel, unitId, timeout, points);
                    }

                    break;
            }
        });

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
    {
        if (ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "responder")) != "modbus")
        {
            return null;
        }

        var unitId = (byte)ZeusConfigurationOptions.GetInt32(channel.Options, "unitId", 1);
        var transport = ZeusConfigurationOptions.GetString(channel.Options, "transport", "rtu");
        return new ModbusSlaveResponder(unitId, CreateChannelTransport(ZeusConfigurationText.Normalize(transport), transport!));
    }

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => ZeusConfigurationOptions.Fingerprint(device.Options);

    private static void Add(
        DeviceConfiguration device,
        Action<string, string, byte, TimeSpan?, Action<ModbusPointMap>?> add)
    {
        Action<ModbusPointMap>? points = device.Points.Count == 0 ? null : map => ApplyPoints(map, device.Points);
        var timeout = ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds") is { } ms ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null;
        var unitId = (byte)ZeusConfigurationOptions.GetInt32(device.Options, "unitId", 1);
        add(device.Name.Trim(), device.Channel.Trim(), unitId, timeout, points);
    }

    private static ModbusTransport CreateDeviceTransport(string normalizedType, string original)
        => normalizedType switch
        {
            "modbus-rtu" => ModbusTransport.Rtu,
            "modbus-tcp" => ModbusTransport.Tcp,
            "modbus-ascii" => ModbusTransport.Ascii,
            _ => throw new ZeusException($"Modbus device type「{original}」不受支持。可选 modbus-rtu、modbus-tcp、modbus-ascii。")
        };

    private static ModbusTransport CreateChannelTransport(string normalizedTransport, string original)
        => normalizedTransport switch
        {
            "rtu" => ModbusTransport.Rtu,
            "tcp" => ModbusTransport.Tcp,
            "ascii" => ModbusTransport.Ascii,
            _ => throw new ZeusException($"Modbus transport「{original}」不受支持。可选 rtu、tcp、ascii。")
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

            var tableValue = ZeusConfigurationOptions.GetString(point.Options, "table", "holding", path);
            var table = ZeusConfigurationText.Normalize(tableValue);
            if (table is not ("holding" or "input" or "coil" or "discrete"))
            {
                throw new ZeusException($"{path}.options.table「{tableValue}」不受支持。可选 holding、input、coil、discrete。");
            }

            var address = ZeusConfigurationOptions.GetInt32(point.Options, "address", path: path);
            if (address is < 0 or > ushort.MaxValue)
            {
                throw new ZeusException($"{path}.options.address 必须介于 0 与 65535 之间。");
            }

            var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale", path);
            if (scale is <= 0)
            {
                throw new ZeusException($"{path}.options.scale 必须大于 0。");
            }

            ZeusConfigurationText.ValidatePointAlarms(point, path);
            if ((ZeusConfigurationOptions.GetNullableDouble(point.Options, "lowAlarmLimit", path) is not null
                    || ZeusConfigurationOptions.GetNullableDouble(point.Options, "highAlarmLimit", path) is not null)
                && table is "coil" or "discrete")
            {
                throw new ZeusException($"{path} 是布尔点，不能配置 options.lowAlarmLimit 或 options.highAlarmLimit。");
            }

            if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable", path: path) && table is "input" or "discrete")
            {
                throw new ZeusException($"{path} 位于只读数据区，不能设置 options.writable: true。");
            }

            if (ZeusConfigurationOptions.GetBoolean(point.Options, "signed", path: path) && table is not ("holding" or "input"))
            {
                throw new ZeusException($"{path}.options.signed 仅适用于 holding 或 input 寄存器。");
            }
        }
    }

    private static void ApplyPoints(ModbusPointMap map, List<PointConfiguration> points)
    {
        foreach (var point in points)
        {
            var table = ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(point.Options, "table", "holding"));
            var alarmLimits = ZeusConfigurationText.CreateAlarmLimits(point);
            var signed = ZeusConfigurationOptions.GetBoolean(point.Options, "signed");
            var writable = ZeusConfigurationOptions.GetBoolean(point.Options, "writable");
            var address = (ushort)ZeusConfigurationOptions.GetInt32(point.Options, "address");
            var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale") ?? (signed ? 1d : (double?)null);
            switch (table)
            {
                case "holding":
                    if (signed)
                    {
                        map.HoldingRegister(point.Name, address, scale!.Value, signed: true, alarmLimits);
                    }
                    else if (scale is { } holdingScale)
                    {
                        map.HoldingRegister(point.Name, address, holdingScale);
                        if (alarmLimits is not null)
                        {
                            map.WithAlarmLimits(point.Name, alarmLimits);
                        }
                    }
                    else
                    {
                        map.HoldingRegister(point.Name, address);
                        if (alarmLimits is not null)
                        {
                            map.WithAlarmLimits(point.Name, alarmLimits);
                        }
                    }

                    if (writable)
                    {
                        map.Writable(point.Name);
                    }

                    break;
                case "input":
                    if (signed)
                    {
                        map.InputRegister(point.Name, address, scale!.Value, signed: true, alarmLimits);
                    }
                    else if (scale is { } inputScale)
                    {
                        map.InputRegister(point.Name, address, inputScale);
                        if (alarmLimits is not null)
                        {
                            map.WithAlarmLimits(point.Name, alarmLimits);
                        }
                    }
                    else
                    {
                        map.InputRegister(point.Name, address);
                        if (alarmLimits is not null)
                        {
                            map.WithAlarmLimits(point.Name, alarmLimits);
                        }
                    }

                    break;
                case "coil":
                    map.Coil(point.Name, address);
                    if (writable)
                    {
                        map.Writable(point.Name);
                    }

                    break;
                case "discrete":
                    map.DiscreteInput(point.Name, address);
                    break;
            }
        }
    }
}
