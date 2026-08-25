using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Zeus;

/// <summary>注册 OPC UA 设备。</summary>
public static class ZeusHostBuilderOpcUaExtensions
{
    /// <summary>在已有通道上登记一台 OPC UA 设备。</summary>
    public static ZeusHostBuilder AddOpcUa(
        this ZeusHostBuilder builder,
        string deviceName,
        string channelName,
        OpcUaOptions? options = null,
        TimeSpan? timeout = null,
        Action<OpcUaPointMap>? points = null)
    {
        return builder.AddDevice(deviceName, channelName, (services, name, channel) =>
            new OpcUaDevice(name, channel, options, timeout, BuildMap(points), services.GetService<ILogger<OpcUaDevice>>()));
    }

    /// <summary>在已构建宿主上登记一台 OPC UA 设备。</summary>
    public static OpcUaDevice AddOpcUa(
        this IZeusHost host,
        string deviceName,
        string channelName,
        OpcUaOptions? options = null,
        TimeSpan? timeout = null,
        Action<OpcUaPointMap>? points = null)
        => host.AddDevice(deviceName, channelName, (services, name, channel) =>
            new OpcUaDevice(name, channel, options, timeout, BuildMap(points), services.GetService<ILogger<OpcUaDevice>>()));

    private static OpcUaPointMap? BuildMap(Action<OpcUaPointMap>? configure)
    {
        if (configure is null)
        {
            return null;
        }

        var map = new OpcUaPointMap();
        configure(map);
        return map;
    }
}
