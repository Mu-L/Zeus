namespace Zeus;

/// <summary>DL/T 645 的 JSON 设备与虚拟表计绑定。由配置核心探测本程序集后登记。</summary>
public sealed class Dlt645JsonBinder : IZeusJsonBinder
{
    /// <inheritdoc />
    public IReadOnlyList<string> DeviceTypes { get; } = ["dlt645"];

    /// <inheritdoc />
    public IReadOnlyList<string> ResponderTypes { get; } = ["dlt645"];

    /// <inheritdoc />
    public void ValidateDevice(DeviceConfiguration device, string path)
    {
        if (ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds", path) is <= 0)
        {
            throw new ZeusException($"{path}.options.timeoutMilliseconds 必须大于 0。");
        }

        ValidateOptions(Options(device, path), $"{path}.options");

        for (var i = 0; i < device.Points.Count; i++)
        {
            var point = device.Points[i];
            var pointPath = $"{path}.points[{i}]";
            ZeusConfigurationText.EnsureName(point.Name, pointPath);
            ValidatePoint(point, pointPath);
        }
    }

    /// <inheritdoc />
    public void ValidateResponder(ChannelConfiguration channel, string path)
    {
        var meterAddress = ZeusConfigurationOptions.GetString(channel.Options, "meterAddress", "000000000001", path)!.Trim();
        ValidateMeterAddress(meterAddress, $"{path}.options.meterAddress");
    }

    /// <inheritdoc />
    public void ApplyDevice(DeviceConfiguration device, ZeusHostBuilder? builder = null, IZeusHost? host = null)
    {
        if (builder is not null)
        {
            builder.AddDlt645(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
            return;
        }

        host!.AddDlt645(device.Name.Trim(), device.Channel.Trim(), Options(device), Timeout(device), Points(device));
    }

    /// <inheritdoc />
    public IVirtualResponder? CreateResponder(ChannelConfiguration channel)
        => ZeusConfigurationText.Normalize(ZeusConfigurationOptions.GetString(channel.Options, "responder")) == "dlt645"
            ? new Dlt645SlaveResponder(ZeusConfigurationOptions.GetString(channel.Options, "meterAddress", "000000000001")!)
            : null;

    /// <inheritdoc />
    public string DeviceFingerprint(DeviceConfiguration device)
        => ZeusConfigurationOptions.Fingerprint(device.Options);

    private static TimeSpan? Timeout(DeviceConfiguration device)
        => ZeusConfigurationOptions.GetNullableInt32(device.Options, "timeoutMilliseconds") is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static Dlt645Options Options(DeviceConfiguration device, string? path = null)
        => new()
        {
            MeterAddress = ZeusConfigurationOptions.GetString(device.Options, "meterAddress", "000000000001", path)!.Trim(),
            WakeUpPreambleCount = ZeusConfigurationOptions.GetInt32(device.Options, "wakeUpPreambleCount", 4, path),
            Password = ZeusConfigurationOptions.GetString(device.Options, "password", "00000000", path)!.Trim(),
            OperatorCode = ZeusConfigurationOptions.GetString(device.Options, "operatorCode", "00000000", path)!.Trim()
        };

    private static Action<Dlt645PointMap>? Points(DeviceConfiguration device)
        => device.Points.Count == 0 ? null : map =>
        {
            foreach (var point in device.Points)
            {
                var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "bcd"), $"point {point.Name}.options.dataType");
                var id = checked((uint)ZeusConfigurationOptions.GetInt32(point.Options, "address"));
                var dataLength = ZeusConfigurationOptions.GetInt32(point.Options, "dataLength", 4);
                if (dataType == Dlt645DataType.RawBytes)
                {
                    map.RawBytes(point.Name, id, dataLength);
                }
                else
                {
                    map.Bcd(point.Name, id, dataLength, ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale") ?? 0.01, ZeusConfigurationText.CreateAlarmLimits(point));
                }

                if (ZeusConfigurationOptions.GetBoolean(point.Options, "writable"))
                {
                    map.Writable(point.Name);
                }
            }
        };

    private static Dlt645DataType ParseDataType(string? value, string path)
        => ZeusConfigurationText.Normalize(value) switch
        {
            "" or "bcd" => Dlt645DataType.Bcd,
            "raw" or "raw-bytes" => Dlt645DataType.RawBytes,
            _ => throw new ZeusException($"{path}「{value}」不受支持。DL/T 645 可选 bcd、raw。")
        };

    private static void ValidateOptions(Dlt645Options options, string path)
    {
        ValidateMeterAddress(options.MeterAddress, $"{path}.meterAddress");
        if (options.WakeUpPreambleCount is < 0 or > 16)
        {
            throw new ZeusException($"{path}.wakeUpPreambleCount 必须介于 0 与 16 之间。");
        }

        try
        {
            _ = Dlt645Codec.EncodeWriteDataRequest(options.MeterAddress, 0, [], options.Password, options.OperatorCode, 0);
        }
        catch (ZeusException ex)
        {
            throw new ZeusException($"{path}.password 或 operatorCode 无效：{ex.Message}", ex);
        }
    }

    private static void ValidateMeterAddress(string meterAddress, string path)
    {
        try
        {
            Dlt645Codec.ValidateAddress(meterAddress);
        }
        catch (ZeusException ex)
        {
            throw new ZeusException($"{path} 无效：{ex.Message}", ex);
        }
    }

    private static void ValidatePoint(PointConfiguration point, string path)
    {
        var dataType = ParseDataType(ZeusConfigurationOptions.GetString(point.Options, "dataType", "bcd", path), $"{path}.options.dataType");
        var address = ZeusConfigurationOptions.GetInt32(point.Options, "address", path: path);
        if (address < 0)
        {
            throw new ZeusException($"{path}.options.address 必须介于 0 与 2147483647 之间。");
        }

        var dataLength = ZeusConfigurationOptions.GetInt32(point.Options, "dataLength", 4, path);
        if (dataLength is < 1 or > 64)
        {
            throw new ZeusException($"{path}.options.dataLength 必须介于 1 与 64 之间。");
        }

        var scale = ZeusConfigurationOptions.GetNullableDouble(point.Options, "scale", path);
        ZeusConfigurationText.ValidatePointAlarms(point, path);
        if (dataType == Dlt645DataType.RawBytes)
        {
            if (scale is not null)
            {
                throw new ZeusException($"{path}.options.scale 只能用于 DL/T 645 bcd 点。");
            }

            if (ZeusConfigurationOptions.GetNullableDouble(point.Options, "lowAlarmLimit", path) is not null
                || ZeusConfigurationOptions.GetNullableDouble(point.Options, "highAlarmLimit", path) is not null)
            {
                throw new ZeusException($"{path}.options.lowAlarmLimit 或 highAlarmLimit 只能用于 DL/T 645 bcd 点。");
            }

            return;
        }

        if (scale is { } factor && (factor <= 0 || !double.IsFinite(factor)))
        {
            throw new ZeusException($"{path}.options.scale 必须是大于 0 的有限数值。");
        }
    }
}
