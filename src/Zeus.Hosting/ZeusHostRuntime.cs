using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeus;

/// <summary>
/// Generic Host 的薄封装。对外只暴露通道、设备与启停，隐藏 IHost 细节。
/// Generic Host 在首次 <see cref="StartAsync"/> 后保持存活；随后的停止只关闭通道并暂停采集，以便再次启动。
/// </summary>
internal sealed class ZeusHostRuntime : IZeusHost
{
    private readonly IHost _host;
    private readonly HostRunState _runState;
    private readonly ILogger<ZeusHostRuntime> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private bool _genericHostStarted;
    private int _disposed;

    /// <summary>
    /// 使用已构建的 Generic Host 与目录创建运行时。
    /// </summary>
    /// <param name="host">底层宿主。</param>
    /// <param name="channels">通道目录。</param>
    /// <param name="devices">设备目录。</param>
    /// <param name="points">点表。</param>
    /// <param name="alarms">报警队列。</param>
    /// <param name="runState">运行闸门。</param>
    public ZeusHostRuntime(
        IHost host,
        IChannelRegistry channels,
        IDeviceRegistry devices,
        IPointTable points,
        IPointAlarmTable alarms,
        HostRunState runState)
    {
        _host = host;
        Channels = channels;
        Devices = devices;
        Points = points;
        Alarms = alarms;
        _runState = runState;
        _logger = host.Services.GetService(typeof(ILogger<ZeusHostRuntime>)) as ILogger<ZeusHostRuntime>
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ZeusHostRuntime>.Instance;
    }

    /// <inheritdoc />
    public IChannelRegistry Channels { get; }

    /// <inheritdoc />
    public IDeviceRegistry Devices { get; }

    /// <inheritdoc />
    public IPointTable Points { get; }

    /// <inheritdoc />
    public IPointAlarmTable Alarms { get; }

    /// <inheritdoc />
    public IServiceProvider Services => _host.Services;

    /// <inheritdoc />
    public bool IsRunning => _runState.IsRunning;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_genericHostStarted)
            {
                await _host.StartAsync(cancellationToken).ConfigureAwait(false);
                _genericHostStarted = true;
            }

            if (_runState.IsRunning)
            {
                return;
            }

            await OpenAllChannelsAsync(cancellationToken).ConfigureAwait(false);
            _runState.MarkStarted();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_runState.IsRunning)
            {
                return;
            }

            _runState.MarkStopped();
            await CloseAllChannelsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync().ConfigureAwait(false);
            if (_genericHostStarted)
            {
                await _host.StopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (_host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                _host.Dispose();
            }

            _lifecycle.Dispose();
        }
    }

    /// <summary>
    /// 按注册顺序打开全部通道。必需通道失败会让宿主启动失败；可选/降级通道失败后继续启动。
    /// </summary>
    private async Task OpenAllChannelsAsync(CancellationToken cancellationToken)
    {
        var opened = new List<IChannel>();
        var requiredFailures = new List<Exception>();
        foreach (var channel in Channels.All)
        {
            try
            {
                await channel.OpenAsync(cancellationToken).ConfigureAwait(false);
                opened.Add(channel);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CloseOpenedChannelsQuietlyAsync(opened).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                // 打开失败已记入通道 Faulted；可选/降级通道会在运行闸门打开后由自动重连重试。
                using var scope = LogScope.Begin(_logger, "Channel", channel.Name);
                if (channel.StartupMode == ChannelStartupMode.Required)
                {
                    _logger.LogError(ZeusLogEvents.ChannelOpenFailed, ex, "必需通道 {Channel} 启动时打开失败，宿主启动将中止。", channel.Name);
                    requiredFailures.Add(ex);
                    continue;
                }

                var mode = channel.StartupMode == ChannelStartupMode.Optional ? "可选" : "降级";
                _logger.LogWarning(ZeusLogEvents.ChannelOpenFailed, ex, "{Mode}通道 {Channel} 启动时打开失败，宿主继续启动并由自动重连重试。", mode, channel.Name);
            }
        }

        if (requiredFailures.Count == 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await CloseOpenedChannelsQuietlyAsync(opened).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return;
        }

        await CloseOpenedChannelsQuietlyAsync(opened).ConfigureAwait(false);

        throw new ZeusException(
            $"宿主启动失败：{requiredFailures.Count} 个必需通道未能打开。请检查通道配置，或把非关键通道设为 Optional/DegradedAllowed。",
            requiredFailures[0]);
    }

    private async Task CloseOpenedChannelsQuietlyAsync(List<IChannel> opened)
    {
        foreach (var channel in opened.AsEnumerable().Reverse())
        {
            try
            {
                await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                using var scope = LogScope.Begin(_logger, "Channel", channel.Name);
                _logger.LogWarning(ZeusLogEvents.ChannelCloseWarning, ex, "宿主启动失败回滚时关闭通道 {Channel} 失败。", channel.Name);
            }
        }
    }

    /// <summary>
    /// 按相反顺序关闭全部通道。
    /// </summary>
    private async Task CloseAllChannelsAsync(CancellationToken cancellationToken)
    {
        foreach (var channel in Channels.All.Reverse())
        {
            try
            {
                await channel.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                using var scope = LogScope.Begin(_logger, "Channel", channel.Name);
                _logger.LogWarning(ZeusLogEvents.ChannelCloseWarning, ex, "通道 {Channel} 停止时关闭失败。", channel.Name);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ZeusHostRuntime), "宿主已释放，不能再次启动。请重新 ZeusHost.Create。");
        }
    }
}
