using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeus;

/// <summary>
/// 读取并校验 Zeus JSON 工程配置。错误消息面向现场工程师，指出文件路径与字段名。
/// 设备与虚拟从站的协议细节由 <see cref="IZeusJsonBinder"/> 处理，本类型只校验通道拓扑与采集选项。
/// </summary>
public static class ZeusConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// 从磁盘读取配置。
    /// </summary>
    /// <param name="path">JSON 文件路径。</param>
    public static ZeusAppConfiguration LoadFile(string path)
        => LoadFile(path, null);

    /// <summary>
    /// 从磁盘读取配置，并使用指定绑定目录校验协议类型。
    /// </summary>
    /// <param name="path">JSON 文件路径。</param>
    /// <param name="registry">协议绑定目录。为 <c>null</c> 时使用全局探测目录。</param>
    public static ZeusAppConfiguration LoadFile(string path, IZeusJsonBinderRegistry? registry)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ZeusException("配置文件路径不能为空。");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new ZeusException($"找不到配置文件 {fullPath}。请确认路径，或先从手册复制一份示例 JSON。");
        }

        string json;
        try
        {
            json = File.ReadAllText(fullPath);
        }
        catch (Exception ex)
        {
            throw new ZeusException($"无法读取配置文件 {fullPath}：{ex.Message}", ex);
        }

        return LoadJson(json, fullPath, registry);
    }

    /// <summary>
    /// 从 JSON 文本读取配置。
    /// </summary>
    /// <param name="json">配置正文。</param>
    /// <param name="sourceName">用于错误消息的来源名，例如文件路径或「内存」。</param>
    public static ZeusAppConfiguration LoadJson(string json, string sourceName = "配置")
        => LoadJson(json, sourceName, null);

    /// <summary>
    /// 从 JSON 文本读取配置，并使用指定绑定目录校验协议类型。
    /// </summary>
    /// <param name="json">配置正文。</param>
    /// <param name="sourceName">用于错误消息的来源名，例如文件路径或「内存」。</param>
    /// <param name="registry">协议绑定目录。为 <c>null</c> 时使用全局探测目录。</param>
    public static ZeusAppConfiguration LoadJson(string json, string sourceName, IZeusJsonBinderRegistry? registry)
    {
        ZeusAppConfiguration? document;
        try
        {
            document = JsonSerializer.Deserialize<ZeusAppConfiguration>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ZeusException(
                $"{sourceName} 不是合法 JSON：{ex.Message} 请检查逗号、引号与注释是否使用 // 或 /* */。",
                ex);
        }

        if (document is null)
        {
            throw new ZeusException($"{sourceName} 解析结果为空。");
        }

        Validate(document, sourceName, registry);
        return document;
    }

    /// <summary>
    /// 校验必填项、名称唯一性与通道引用。协议字段交给已登记的 JSON 绑定。
    /// </summary>
    /// <param name="document">已反序列化的配置。</param>
    /// <param name="sourceName">来源名。</param>
    public static void Validate(ZeusAppConfiguration document, string sourceName = "配置")
        => Validate(document, sourceName, null);

    /// <summary>
    /// 校验必填项、名称唯一性与通道引用。协议字段交给已登记的 JSON 绑定。
    /// </summary>
    /// <param name="document">已反序列化的配置。</param>
    /// <param name="sourceName">来源名。</param>
    /// <param name="registry">协议绑定目录。为 <c>null</c> 时使用全局探测目录。</param>
    public static void Validate(ZeusAppConfiguration document, string sourceName, IZeusJsonBinderRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Acquisition.IntervalMilliseconds <= 0)
        {
            throw new ZeusException($"{sourceName} 中 acquisition.intervalMilliseconds 必须大于 0。");
        }

        if (document.Reconnect.InitialDelayMilliseconds < 0)
        {
            throw new ZeusException($"{sourceName} 中 reconnect.initialDelayMilliseconds 不能为负数。");
        }

        if (document.Reconnect.MaxDelayMilliseconds < 0)
        {
            throw new ZeusException($"{sourceName} 中 reconnect.maxDelayMilliseconds 不能为负数。");
        }

        if (document.Reconnect.BackoffMultiplier < 1)
        {
            throw new ZeusException($"{sourceName} 中 reconnect.backoffMultiplier 必须大于或等于 1。");
        }

        if (document.Reconnect.MaxAttempts < 0)
        {
            throw new ZeusException($"{sourceName} 中 reconnect.maxAttempts 不能为负数。");
        }

        if (document.Reconnect.JitterRatio < 0 || !double.IsFinite(document.Reconnect.JitterRatio))
        {
            throw new ZeusException($"{sourceName} 中 reconnect.jitterRatio 必须是大于或等于 0 的有限数值。");
        }

        if (document.Reconnect.CircuitBreakMilliseconds < 0)
        {
            throw new ZeusException($"{sourceName} 中 reconnect.circuitBreakMilliseconds 不能为负数。");
        }

        if (document.Acquisition.SourceTimeoutMilliseconds < 0)
        {
            throw new ZeusException($"{sourceName} 中 acquisition.sourceTimeoutMilliseconds 不能为负数。");
        }

        var channelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < document.Channels.Count; i++)
        {
            var channel = document.Channels[i];
            var path = $"{sourceName} channels[{i}]";
            ZeusConfigurationText.EnsureName(channel.Name, path);
            if (!channelNames.Add(channel.Name.Trim()))
            {
                throw new ZeusException($"{path}.name「{channel.Name}」重复。每个通道名在文件内必须唯一。");
            }

            switch (ZeusConfigurationText.Normalize(channel.Type))
            {
                case "virtual":
                    ValidateVirtual(channel, path, registry);
                    break;
                case "serial":
                    if (string.IsNullOrWhiteSpace(ZeusConfigurationOptions.GetString(channel.Options, "portName", path: path)))
                    {
                        throw new ZeusException($"{path} 类型为 serial 时必须提供 options.portName，例如 COM3。");
                    }

                    if (ZeusConfigurationOptions.GetInt32(channel.Options, "baudRate", 115200, path) <= 0)
                    {
                        throw new ZeusException($"{path}.options.baudRate 必须大于 0。");
                    }

                    break;
                case "tcp":
                    ValidateNetworkChannel(channel, path, "tcp");
                    break;
                case "udp":
                    ValidateNetworkChannel(channel, path, "udp");
                    var localPort = ZeusConfigurationOptions.GetInt32(channel.Options, "localPort", 0, path);
                    if (localPort is < 0 or > 65535)
                    {
                        throw new ZeusException($"{path}.options.localPort 必须介于 0 与 65535 之间，0 表示自动分配。");
                    }

                    break;
                case "tcp-server":
                    ValidateServerChannel(channel, path, "tcp-server");
                    break;
                case "udp-server":
                    ValidateServerChannel(channel, path, "udp-server");
                    break;
                default:
                    throw new ZeusException(
                        $"{path}.type「{channel.Type}」不受支持。可选 virtual、serial、tcp、tcp-server、udp、udp-server。");
            }

            _ = ZeusConfigurationText.ParseStartupMode(channel.Startup, $"{path}.startup");
        }

        var deviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < document.Devices.Count; i++)
        {
            var device = document.Devices[i];
            var path = $"{sourceName} devices[{i}]";
            ZeusConfigurationText.EnsureName(device.Name, path);
            if (!deviceNames.Add(device.Name.Trim()))
            {
                throw new ZeusException($"{path}.name「{device.Name}」重复。");
            }

            if (string.IsNullOrWhiteSpace(device.Channel))
            {
                throw new ZeusException($"{path} 必须指定 channel。");
            }

            if (!channelNames.Contains(device.Channel.Trim()))
            {
                throw new ZeusException(
                    $"{path}.channel「{device.Channel}」未在 channels 中声明。请先写通道，再写设备。");
            }

            var type = ZeusConfigurationText.Normalize(device.Type);
            var binder = registry?.FindDevice(type) ?? ZeusJsonBinders.FindDevice(type);
            if (binder is null)
            {
                var knownBinders = registry?.All ?? ZeusJsonBinders.All;
                var known = string.Join("、", knownBinders.SelectMany(item => item.DeviceTypes).Distinct());
                var hint = ZeusJsonBinders.MissingDevicePackageMessage(type);
                throw new ZeusException(
                    $"{path}.type「{device.Type}」没有对应的 JSON 绑定。{hint}（当前已加载：{(string.IsNullOrEmpty(known) ? "无" : known)}）。");
            }

            binder.ValidateDevice(device, path);
        }
    }

    private static void ValidateVirtual(ChannelConfiguration channel, string path, IZeusJsonBinderRegistry? registry)
    {
        var responderValue = ZeusConfigurationOptions.GetString(channel.Options, "responder", path: path);
        if (string.IsNullOrWhiteSpace(responderValue))
        {
            return;
        }

        var responder = ZeusConfigurationText.Normalize(responderValue);
        var binder = registry?.FindResponder(responder) ?? ZeusJsonBinders.FindResponder(responder);
        if (binder is null)
        {
            var knownBinders = registry?.All ?? ZeusJsonBinders.All;
            var known = string.Join("、", knownBinders.SelectMany(item => item.ResponderTypes).Distinct());
            var hint = ZeusJsonBinders.MissingResponderPackageMessage(responder);
            throw new ZeusException(
                $"{path}.options.responder「{responderValue}」没有对应的 JSON 绑定。{hint}，或省略 responder 以回显写入。当前已加载：{(string.IsNullOrEmpty(known) ? "无" : known)}。");
        }

        binder.ValidateResponder(channel, path);
    }

    private static void ValidateNetworkChannel(ChannelConfiguration channel, string path, string type)
    {
        if (string.IsNullOrWhiteSpace(ZeusConfigurationOptions.GetString(channel.Options, "host", path: path)))
        {
            throw new ZeusException($"{path} 类型为 {type} 时必须提供 options.host。");
        }

        var port = ZeusConfigurationOptions.GetInt32(channel.Options, "port", 502, path);
        if (port is <= 0 or > 65535)
        {
            throw new ZeusException($"{path}.options.port 必须介于 1 与 65535 之间。");
        }
    }

    private static void ValidateServerChannel(ChannelConfiguration channel, string path, string type)
    {
        var localAddress = ZeusConfigurationOptions.GetString(channel.Options, "localAddress", path: path);
        if (!string.IsNullOrWhiteSpace(localAddress)
            && !IPAddress.TryParse(localAddress.Trim(), out _))
        {
            throw new ZeusException($"{path}.options.localAddress 必须是有效 IP 地址，例如 0.0.0.0 或 127.0.0.1。");
        }

        var localPort = ZeusConfigurationOptions.GetInt32(channel.Options, "localPort", 0, path);
        if (localPort is < 0 or > 65535)
        {
            throw new ZeusException($"{path}.options.localPort 必须介于 0 与 65535 之间，0 表示自动分配。{type} 只认 localPort，不能把 port 当作监听端口。");
        }
    }
}
