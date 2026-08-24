using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Zeus;

/// <summary>
/// 注册 Omron FINS 设备与虚拟 PLC。通道必须先注册。
/// </summary>
public static class ZeusHostBuilderFinsExtensions
{
    /// <summary>在已有通道上登记一台 Omron FINS 设备。</summary>
    public static ZeusHostBuilder AddOmronFins(
        this ZeusHostBuilder builder,
        string deviceName,
        string channelName,
        FinsTransport transport,
        FinsOptions? options = null,
        TimeSpan? timeout = null,
        Action<FinsPointMap>? points = null)
    {
        return builder.AddDevice(deviceName, channelName, (services, name, channel) =>
            new FinsDevice(name, channel, transport, options, timeout, BuildMap(points), services.GetService<ILogger<FinsDevice>>()));
    }

    /// <summary>在已构建的宿主上登记一台 Omron FINS 设备。</summary>
    public static FinsDevice AddOmronFins(
        this IZeusHost host,
        string deviceName,
        string channelName,
        FinsTransport transport,
        FinsOptions? options = null,
        TimeSpan? timeout = null,
        Action<FinsPointMap>? points = null)
        => host.AddDevice(deviceName, channelName, (services, name, channel) =>
            new FinsDevice(name, channel, transport, options, timeout, BuildMap(points), services.GetService<ILogger<FinsDevice>>()));

    private static FinsPointMap? BuildMap(Action<FinsPointMap>? configure)
    {
        if (configure is null)
        {
            return null;
        }

        var map = new FinsPointMap();
        configure(map);
        return map;
    }
}
