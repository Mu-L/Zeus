using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeus;

/// <summary>
/// 监视通道故障并按指数退避自动 <see cref="IChannel.OpenAsync"/>。
/// 主动关闭、宿主停止或选项关闭时不会重连。
/// </summary>
internal sealed class ChannelReconnectService : IHostedService, IDisposable
{
    private readonly ChannelRegistry _channels;
    private readonly ChannelReconnectOptions _options;
    private readonly HostRunState _runState;
    private readonly ILogger<ChannelReconnectService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, ReconnectAttempt> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<IChannel> _subscribed = [];

    /// <summary>
    /// 初始化自动重连服务。
    /// </summary>
    public ChannelReconnectService(
        ChannelRegistry channels,
        ChannelReconnectOptions options,
        HostRunState runState,
        ILogger<ChannelReconnectService> logger)
    {
        _channels = channels;
        _options = options;
        _runState = runState;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _channels.Changed += OnRegistryChanged;
        _runState.Started += OnHostStarted;
        _runState.Stopped += OnHostStopped;
        foreach (var channel in _channels.All)
        {
            Subscribe(channel);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _channels.Changed -= OnRegistryChanged;
        _runState.Started -= OnHostStarted;
        _runState.Stopped -= OnHostStopped;
        CancelAll();
        foreach (var channel in _subscribed.ToArray())
        {
            Unsubscribe(channel);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => CancelAll();

    private void OnHostStarted(object? sender, EventArgs e)
    {
        foreach (var channel in _channels.All)
        {
            if (channel.State == ChannelState.Faulted)
            {
                Schedule(channel);
            }
        }
    }

    private void OnHostStopped(object? sender, EventArgs e) => CancelAll();

    private void OnRegistryChanged(object? sender, ChannelRegistryChangedEventArgs e)
    {
        if (e.Change == ChannelRegistryChange.Added)
        {
            Subscribe(e.Channel);
            if (e.Channel.State == ChannelState.Faulted && _runState.IsRunning)
            {
                Schedule(e.Channel);
            }

            return;
        }

        Unsubscribe(e.Channel);
        CancelAndRemove(e.Channel.Name, publishCancelled: true);
    }

    private void Subscribe(IChannel channel)
    {
        lock (_gate)
        {
            if (!_subscribed.Add(channel))
            {
                return;
            }
        }

        channel.StateChanged += OnStateChanged;
    }

    private void Unsubscribe(IChannel channel)
    {
        lock (_gate)
        {
            _subscribed.Remove(channel);
        }

        channel.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged(object? sender, ChannelStateChangedEventArgs e)
    {
        if (sender is not IChannel channel)
        {
            return;
        }

        if (e.Current == ChannelState.Faulted)
        {
            Schedule(channel);
            return;
        }

        // Opening 是本轮 OpenAsync 的中间态，不能取消正在使用的令牌。
        if (e.Current == ChannelState.Opening)
        {
            return;
        }

        if (e.Current == ChannelState.Open)
        {
            CompleteAttempt(channel.Name);
            return;
        }

        if (e.Current == ChannelState.Closed)
        {
            CancelAndRemove(channel.Name, publishCancelled: true);
            return;
        }

        Cancel(channel.Name);
    }

    private void Schedule(IChannel channel)
    {
        if (!_options.Enabled || !_runState.IsRunning)
        {
            return;
        }

        CancellationToken token = default;
        CancellationTokenSource? cts = null;
        TimeSpan delay = default;
        int attempt = 0;
        ReconnectStateChangedEventArgs? notification = null;
        var openCircuit = false;
        var scheduleReconnect = false;
        lock (_gate)
        {
            if (_attempts.TryGetValue(channel.Name, out var existing) && existing.Pending)
            {
                return;
            }

            existing?.Cts.Cancel();
            existing?.Cts.Dispose();

            var next = (existing?.Count ?? 0) + 1;
            if (_options.MaxAttempts > 0 && next > _options.MaxAttempts)
            {
                var circuitDelay = _options.CircuitBreakDuration;
                if (circuitDelay > TimeSpan.Zero)
                {
                    cts = new CancellationTokenSource();
                    _attempts[channel.Name] = new ReconnectAttempt(0, cts, Pending: true);
                    token = cts.Token;
                    delay = circuitDelay;
                    notification = new ReconnectStateChangedEventArgs(
                        channel.Name,
                        ReconnectState.CircuitOpen,
                        next - 1,
                        circuitDelay);
                    openCircuit = true;
                }
                else
                {
                    _attempts.Remove(channel.Name);
                    notification = new ReconnectStateChangedEventArgs(channel.Name, ReconnectState.GaveUp, next - 1);
                }
            }
            else
            {
                cts = new CancellationTokenSource();
                _attempts[channel.Name] = new ReconnectAttempt(next, cts, Pending: true);
                token = cts.Token;
                delay = ComputeDelay(next);
                attempt = next;
                notification = new ReconnectStateChangedEventArgs(channel.Name, ReconnectState.Scheduled, attempt, delay);
                scheduleReconnect = true;
            }
        }

        if (notification is null)
        {
            return;
        }

        if (scheduleReconnect)
        {
            using (LogScope.Begin(_logger, "Channel", channel.Name))
            {
                _logger.LogWarning(
                    ZeusLogEvents.ReconnectScheduled,
                    "通道 {Channel} 已故障，将在 {Delay} ms 后进行第 {Attempt} 次自动重连。",
                    channel.Name,
                    (int)delay.TotalMilliseconds,
                    attempt);
            }
        }

        _options.PublishState(notification);
        if (openCircuit)
        {
            _ = OpenCircuitAsync(channel, delay, cts!, token);
            return;
        }

        if (!scheduleReconnect)
        {
            return;
        }

        _ = ReconnectAsync(channel, delay, cts!, token);
    }

    private async Task OpenCircuitAsync(
        IChannel channel,
        TimeSpan delay,
        CancellationTokenSource owner,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            var shouldRetry = false;
            lock (_gate)
            {
                if (_attempts.TryGetValue(channel.Name, out var current) && ReferenceEquals(current.Cts, owner))
                {
                    _attempts[channel.Name] = current with { Pending = false };
                    shouldRetry = channel.State == ChannelState.Faulted
                        && _runState.IsRunning
                        && _options.Enabled
                        && !cancellationToken.IsCancellationRequested;
                }
            }

            if (shouldRetry)
            {
                Schedule(channel);
            }
        }
    }

    private async Task ReconnectAsync(
        IChannel channel,
        TimeSpan delay,
        CancellationTokenSource owner,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            if (!_runState.IsRunning || channel.State != ChannelState.Faulted)
            {
                return;
            }

            await channel.OpenAsync(cancellationToken).ConfigureAwait(false);
            using (LogScope.Begin(_logger, "Channel", channel.Name))
            {
                _logger.LogInformation(ZeusLogEvents.ReconnectSucceeded, "通道 {Channel} 已自动重连。", channel.Name);
            }
            _options.PublishState(new ReconnectStateChangedEventArgs(channel.Name, ReconnectState.Succeeded, 0));
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            using var scope = LogScope.Begin(_logger, "Channel", channel.Name);
            _logger.LogWarning(ZeusLogEvents.ReconnectFailed, ex, "通道 {Channel} 自动重连失败，将继续退避重试。", channel.Name);
            _options.PublishState(new ReconnectStateChangedEventArgs(channel.Name, ReconnectState.Failed, 0, error: ex));
        }
        finally
        {
            var shouldRetry = false;
            lock (_gate)
            {
                if (_attempts.TryGetValue(channel.Name, out var current) && ReferenceEquals(current.Cts, owner))
                {
                    _attempts[channel.Name] = current with { Pending = false };
                    shouldRetry = channel.State == ChannelState.Faulted
                        && _runState.IsRunning
                        && _options.Enabled
                        && !cancellationToken.IsCancellationRequested;
                }
            }

            // OpenAsync 失败时状态仍是 Faulted；打开过程中的 Opening 会取消本次排队。
            // 仅当本轮仍是当前尝试时再补一次，避免冲掉已经启动的下一轮。
            if (shouldRetry)
            {
                Schedule(channel);
            }
        }
    }

    private TimeSpan ComputeDelay(int attempt)
    {
        var initial = _options.InitialDelay < TimeSpan.Zero ? TimeSpan.FromSeconds(1) : _options.InitialDelay;
        var max = _options.MaxDelay < initial ? initial : _options.MaxDelay;
        var multiplier = _options.BackoffMultiplier < 1 ? 1 : _options.BackoffMultiplier;
        var factor = Math.Pow(multiplier, Math.Max(0, attempt - 1));
        if (double.IsInfinity(factor) || double.IsNaN(factor))
        {
            return max;
        }

        var millis = initial.TotalMilliseconds * factor;
        if (millis > max.TotalMilliseconds)
        {
            millis = max.TotalMilliseconds;
        }

        var jitterRatio = _options.JitterRatio;
        if (double.IsFinite(jitterRatio) && jitterRatio > 0)
        {
            jitterRatio = Math.Min(1, jitterRatio);
            var offset = (Random.Shared.NextDouble() * 2d) - 1d;
            millis *= 1d + (offset * jitterRatio);
            millis = Math.Max(1, Math.Min(max.TotalMilliseconds, millis));
        }

        return TimeSpan.FromMilliseconds(millis);
    }

    private void CompleteAttempt(string name)
    {
        lock (_gate)
        {
            if (_attempts.Remove(name, out var attempt))
            {
                attempt.Cts.Dispose();
            }
        }
    }

    private void CancelAndRemove(string name, bool publishCancelled)
    {
        ReconnectStateChangedEventArgs? notification = null;
        lock (_gate)
        {
            if (_attempts.Remove(name, out var attempt))
            {
                attempt.Cts.Cancel();
                attempt.Cts.Dispose();
                if (publishCancelled)
                {
                    notification = new ReconnectStateChangedEventArgs(name, ReconnectState.Cancelled, attempt.Count);
                }
            }
        }

        if (notification is not null)
        {
            _options.PublishState(notification);
        }
    }

    private void Cancel(string name)
    {
        ReconnectStateChangedEventArgs? notification = null;
        lock (_gate)
        {
            if (_attempts.TryGetValue(name, out var attempt))
            {
                attempt.Cts.Cancel();
                _attempts[name] = attempt with { Pending = false };
                notification = new ReconnectStateChangedEventArgs(name, ReconnectState.Cancelled, attempt.Count);
            }
        }

        if (notification is not null)
        {
            _options.PublishState(notification);
        }
    }

    private void CancelAll()
    {
        lock (_gate)
        {
            foreach (var attempt in _attempts.Values)
            {
                attempt.Cts.Cancel();
                attempt.Cts.Dispose();
            }

            _attempts.Clear();
        }
    }

    private sealed record ReconnectAttempt(int Count, CancellationTokenSource Cts, bool Pending);
}
