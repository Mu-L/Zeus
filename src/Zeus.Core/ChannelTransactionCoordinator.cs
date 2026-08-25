using System.Runtime.CompilerServices;

namespace Zeus;

/// <summary>
/// 按物理通道串行化请求/响应事务，避免同一总线上的多个协议客户端互相抢帧。
/// </summary>
public sealed class ChannelTransactionCoordinator
{
    private static readonly ConditionalWeakTable<IChannel, ChannelTransactionCoordinator> Coordinators = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ChannelTransactionCoordinator(IChannel channel) => Channel = channel;

    /// <summary>被协调的通道。</summary>
    public IChannel Channel { get; }

    /// <summary>获取某个通道的共享事务协调器。</summary>
    public static ChannelTransactionCoordinator For(IChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return Coordinators.GetValue(channel, static item => new ChannelTransactionCoordinator(item));
    }

    /// <summary>进入该通道的独占事务。</summary>
    public Task WaitAsync(CancellationToken cancellationToken = default)
        => _gate.WaitAsync(cancellationToken);

    /// <summary>尝试在指定时间内进入该通道的独占事务。</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _gate.WaitAsync(timeout, cancellationToken);

    /// <summary>尝试在指定毫秒数内进入该通道的独占事务。</summary>
    public Task<bool> WaitAsync(int millisecondsTimeout, CancellationToken cancellationToken = default)
        => _gate.WaitAsync(millisecondsTimeout, cancellationToken);

    /// <summary>退出该通道的独占事务。</summary>
    public void Release() => _gate.Release();
}
