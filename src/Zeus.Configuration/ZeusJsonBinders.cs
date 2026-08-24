using System.Reflection;

namespace Zeus;

/// <summary>
/// JSON 协议绑定目录。装载配置前探测输出目录中的官方协议包并登记绑定。
/// </summary>
public static class ZeusJsonBinders
{
    private static readonly object Gate = new();
    private static readonly List<IZeusJsonBinder> Binders = [];
    private static readonly string[] WellKnownAssemblies =
    [
        "Zeus.Protocols.Modbus",
        "Zeus.Protocols.Mc",
        "Zeus.Protocols.S7",
        "Zeus.Protocols.Fins",
        "Zeus.Protocols.HostLink",
        "Zeus.Protocols.Mewtocol",
        "Zeus.Protocols.EtherNetIp",
        "Zeus.Protocols.Dlt645",
        "Zeus.Protocols.Iec104",
        "Zeus.Protocols.Mqtt",
        "Zeus.Protocols.Snmp"
    ];

    private static readonly Dictionary<string, string> DevicePackageHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["modbus-rtu"] = "Zeus.Protocols.Modbus",
        ["modbus-tcp"] = "Zeus.Protocols.Modbus",
        ["modbus-ascii"] = "Zeus.Protocols.Modbus",
        ["mitsubishi-mc"] = "Zeus.Protocols.Mc",
        ["siemens-s7"] = "Zeus.Protocols.S7",
        ["omron-fins"] = "Zeus.Protocols.Fins",
        ["omron-host-link"] = "Zeus.Protocols.HostLink",
        ["panasonic-mewtocol"] = "Zeus.Protocols.Mewtocol",
        ["ethernet-ip"] = "Zeus.Protocols.EtherNetIp",
        ["dlt645"] = "Zeus.Protocols.Dlt645",
        ["iec104"] = "Zeus.Protocols.Iec104",
        ["mqtt"] = "Zeus.Protocols.Mqtt",
        ["snmp"] = "Zeus.Protocols.Snmp"
    };

    private static readonly Dictionary<string, string> ResponderPackageHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["modbus"] = "Zeus.Protocols.Modbus",
        ["mc"] = "Zeus.Protocols.Mc",
        ["s7"] = "Zeus.Protocols.S7",
        ["fins"] = "Zeus.Protocols.Fins",
        ["host-link"] = "Zeus.Protocols.HostLink",
        ["mewtocol"] = "Zeus.Protocols.Mewtocol",
        ["ethernet-ip"] = "Zeus.Protocols.EtherNetIp",
        ["dlt645"] = "Zeus.Protocols.Dlt645",
        ["iec104"] = "Zeus.Protocols.Iec104",
        ["mqtt"] = "Zeus.Protocols.Mqtt",
        ["snmp"] = "Zeus.Protocols.Snmp"
    };

    /// <summary>
    /// 登记一个协议绑定。重复登记同一实例会被忽略。
    /// </summary>
    /// <param name="binder">协议绑定。</param>
    public static void Register(IZeusJsonBinder binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        lock (Gate)
        {
            if (Binders.Any(existing => ReferenceEquals(existing, binder) || existing.GetType() == binder.GetType()))
            {
                return;
            }

            Binders.Add(binder);
        }
    }

    /// <summary>
    /// 探测已加载及输出目录中的官方协议程序集，并登记其中的 JSON 绑定。
    /// 只引用配置包、尚未碰到协议类型时必须先探测，否则 JSON 设备类型无法解析。
    /// </summary>
    public static void Probe()
    {
        foreach (var name in WellKnownAssemblies)
        {
            try
            {
                var assembly = Assembly.Load(name);
                foreach (var type in assembly.GetExportedTypes())
                {
                    if (!typeof(IZeusJsonBinder).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                    {
                        continue;
                    }

                    Register((IZeusJsonBinder)Activator.CreateInstance(type)!);
                }
            }
            catch (FileNotFoundException)
            {
                // 未引用的协议包不在输出目录，忽略；已存在但加载失败的问题必须暴露出来。
            }
        }
    }

    /// <summary>
    /// 按设备类型查找绑定。
    /// </summary>
    public static IZeusJsonBinder? FindDevice(string normalizedType)
    {
        Probe();
        lock (Gate)
        {
            return Binders.FirstOrDefault(binder =>
                binder.DeviceTypes.Any(type => string.Equals(type, normalizedType, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>
    /// 按虚拟从站类型查找绑定。
    /// </summary>
    public static IZeusJsonBinder? FindResponder(string normalizedResponder)
    {
        Probe();
        lock (Gate)
        {
            return Binders.FirstOrDefault(binder =>
                binder.ResponderTypes.Any(type => string.Equals(type, normalizedResponder, StringComparison.OrdinalIgnoreCase)));
        }
    }

    internal static string MissingDevicePackageMessage(string normalizedType)
        => DevicePackageHints.TryGetValue(normalizedType, out var package)
            ? $"请安装协议包：dotnet add package {package}"
            : "请引用对应协议包";

    internal static string MissingResponderPackageMessage(string normalizedResponder)
        => ResponderPackageHints.TryGetValue(normalizedResponder, out var package)
            ? $"请安装协议包：dotnet add package {package}"
            : "请引用对应协议包";

    /// <summary>当前已登记绑定的快照。</summary>
    public static IReadOnlyList<IZeusJsonBinder> All
    {
        get
        {
            Probe();
            lock (Gate)
            {
                return Binders.ToArray();
            }
        }
    }
}
