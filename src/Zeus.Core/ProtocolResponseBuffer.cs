namespace Zeus;

/// <summary>
/// 协议客户端共用的接收缓冲与等待脉冲。
/// 调用方在 <see cref="SyncRoot"/> 内解码 <see cref="Bytes"/>，避免每个协议重复处理半包、溢出和通道中断。
/// </summary>
internal sealed class ProtocolResponseBuffer
{
    private readonly IChannel _channel;
    private readonly int _maxBytes;
    private TaskCompletionSource<bool>? _dataPulse;

    public ProtocolResponseBuffer(IChannel channel, int maxBytes = ProtocolReceiveBuffer.DefaultMaxBytes)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _maxBytes = maxBytes;
    }

    public object SyncRoot { get; } = new();

    public List<byte> Bytes { get; } = [];

    public void Clear()
    {
        lock (SyncRoot)
        {
            Bytes.Clear();
        }
    }

    public void ClearLocked() => Bytes.Clear();

    public Task WaitForDataLocked()
    {
        _dataPulse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _dataPulse.Task;
    }

    public void Append(ReadOnlyMemory<byte> data)
    {
        lock (SyncRoot)
        {
            if (!ProtocolReceiveBuffer.TryAppend(Bytes, data.Span, _maxBytes))
            {
                _dataPulse?.TrySetException(ProtocolReceiveBuffer.Overflow(_channel.Name, _maxBytes));
                _dataPulse = null;
                return;
            }

            _dataPulse?.TrySetResult(true);
            _dataPulse = null;
        }
    }

    public void CancelPending(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (SyncRoot)
        {
            CancelPendingLocked(exception);
        }
    }

    public void CancelPendingLocked(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Bytes.Clear();
        _dataPulse?.TrySetException(exception);
        _dataPulse = null;
    }
}
