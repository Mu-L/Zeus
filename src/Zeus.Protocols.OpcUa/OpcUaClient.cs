namespace Zeus;

/// <summary>
/// 在一条 Zeus 通道上执行 OPC UA Binary 会话。
/// 同一客户端串行发送 HEL/ACK、安全通道、会话和 Read/Write，适合 TCP 或虚拟通道。
/// </summary>
public sealed class OpcUaClient : IAsyncDisposable
{
    private readonly IChannel _channel;
    private readonly OpcUaOptions _options;
    private readonly TimeSpan _timeout;
    private readonly ChannelTransactionCoordinator _gate;
    private readonly ProtocolResponseBuffer _receive;
    private readonly string _fallbackName;
    private uint _requestId;
    private uint _sequenceNumber;
    private uint _channelId;
    private uint _tokenId;
    private OpcUaNodeId? _authenticationToken;
    private int _sessionActive;
    private int _disposed;
    private bool _everConnected;

    /// <summary>创建 OPC UA 客户端并订阅通道。</summary>
    public OpcUaClient(IChannel channel, OpcUaOptions? options = null, TimeSpan? timeout = null, string? fallbackName = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _gate = ChannelTransactionCoordinator.For(_channel);
        _receive = new ProtocolResponseBuffer(_channel);
        _options = CopyOptions(options ?? new OpcUaOptions());
        OpcUaCodec.ValidateOptions(_options);
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "OPC UA 超时必须大于 0。");
        }

        _fallbackName = string.IsNullOrWhiteSpace(fallbackName) ? "zeus-opcua" : fallbackName.Trim();
        _channel.DataReceived += OnDataReceived;
        _channel.StateChanged += OnStateChanged;
    }

    /// <summary>绑定的通道。</summary>
    public IChannel Channel => _channel;

    /// <summary>当前会话选项副本。</summary>
    public OpcUaOptions Options => CopyOptions(_options);

    /// <summary>是否已完成 ActivateSession。</summary>
    public bool IsSessionActive => Volatile.Read(ref _sessionActive) != 0;

    /// <summary>发送 HEL/OPN/CreateSession/ActivateSession，直到会话可用。</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSessionLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>读取一个节点的 Value 属性。</summary>
    public async Task<OpcUaValue> ReadAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        var values = await ReadAsync([OpcUaNodeId.Parse(nodeId)], cancellationToken).ConfigureAwait(false);
        return values[0];
    }

    /// <summary>批量读取多个节点的 Value 属性。</summary>
    public async Task<IReadOnlyList<OpcUaValue>> ReadAsync(IReadOnlyList<OpcUaNodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        if (nodeIds.Count == 0)
        {
            throw new OpcUaException("OPC UA 读取列表不能为空。");
        }

        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSessionLockedAsync(cancellationToken).ConfigureAwait(false);
            var requestId = NextRequestId();
            await _channel.WriteAsync(
                OpcUaCodec.EncodeReadRequest(_channelId, _tokenId, requestId, NextSequenceNumber(), _authenticationToken!, nodeIds),
                cancellationToken).ConfigureAwait(false);
            var response = await WaitForServiceAsync(OpcUaCodec.TypeReadResponse, requestId, "Read", cancellationToken).ConfigureAwait(false);
            if (response.Values is null || response.Values.Count != nodeIds.Count)
            {
                throw new OpcUaException("OPC UA Read 响应数量与请求不一致。");
            }

            return response.Values;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>写入一个节点的 Value 属性。</summary>
    public Task WriteAsync(string nodeId, OpcUaValue value, CancellationToken cancellationToken = default)
        => WriteAsync([(OpcUaNodeId.Parse(nodeId), value)], cancellationToken);

    /// <summary>批量写入多个节点的 Value 属性。</summary>
    public async Task WriteAsync(IReadOnlyList<(OpcUaNodeId NodeId, OpcUaValue Value)> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new OpcUaException("OPC UA 写入列表不能为空。");
        }

        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSessionLockedAsync(cancellationToken).ConfigureAwait(false);
            var requestId = NextRequestId();
            await _channel.WriteAsync(
                OpcUaCodec.EncodeWriteRequest(_channelId, _tokenId, requestId, NextSequenceNumber(), _authenticationToken!, items),
                cancellationToken).ConfigureAwait(false);
            var response = await WaitForServiceAsync(OpcUaCodec.TypeWriteResponse, requestId, "Write", cancellationToken).ConfigureAwait(false);
            if (response.Values is null || response.Values.Count != items.Count)
            {
                throw new OpcUaException("OPC UA Write 响应数量与请求不一致。");
            }

            for (var i = 0; i < response.Values.Count; i++)
            {
                var status = response.Values[i].StatusCode;
                if (!OpcUaStatusCodes.IsGood(status))
                {
                    throw new OpcUaException(status, $"写入 {items[i].NodeId} 失败，状态码 0x{status:X8}。");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>读取文本节点。</summary>
    public async Task<string> ReadTextAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        var value = await ReadAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return Convert.ToString(OpcUaCodec.ToEngineeringValue(value, null), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _channel.DataReceived -= OnDataReceived;
        _channel.StateChanged -= OnStateChanged;
        ResetSession();
        return ValueTask.CompletedTask;
    }

    private async Task EnsureSessionLockedAsync(CancellationToken cancellationToken)
    {
        if (IsSessionActive)
        {
            return;
        }

        if (_everConnected && !_options.AutomaticReconnect)
        {
            throw new OpcUaException("OPC UA 会话已断开，且未启用自动重连。");
        }

        ResetSession();
        await _channel.WriteAsync(
            OpcUaCodec.EncodeHello(string.IsNullOrWhiteSpace(_options.EndpointUrl) ? OpcUaCodec.DefaultEndpointUrl : _options.EndpointUrl),
            cancellationToken).ConfigureAwait(false);
        _ = await WaitForMessageAsync("ACK", 0, "Hello", cancellationToken).ConfigureAwait(false);

        var openId = NextRequestId();
        await _channel.WriteAsync(OpcUaCodec.EncodeOpenSecureChannelRequest(openId, NextSequenceNumber()), cancellationToken).ConfigureAwait(false);
        var open = await WaitForServiceAsync(OpcUaCodec.TypeOpenSecureChannelResponse, openId, "OpenSecureChannel", cancellationToken).ConfigureAwait(false);
        _channelId = open.ChannelId;
        _tokenId = open.TokenId ?? 1;

        var createId = NextRequestId();
        await _channel.WriteAsync(
            OpcUaCodec.EncodeCreateSessionRequest(_channelId, _tokenId, createId, NextSequenceNumber(), _options, _fallbackName),
            cancellationToken).ConfigureAwait(false);
        var created = await WaitForServiceAsync(OpcUaCodec.TypeCreateSessionResponse, createId, "CreateSession", cancellationToken).ConfigureAwait(false);
        _authenticationToken = created.AuthenticationToken ?? throw new OpcUaException("OPC UA CreateSession 未返回 AuthenticationToken。");

        var activateId = NextRequestId();
        await _channel.WriteAsync(
            OpcUaCodec.EncodeActivateSessionRequest(_channelId, _tokenId, activateId, NextSequenceNumber(), _authenticationToken, _options),
            cancellationToken).ConfigureAwait(false);
        _ = await WaitForServiceAsync(OpcUaCodec.TypeActivateSessionResponse, activateId, "ActivateSession", cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _sessionActive, 1);
        _everConnected = true;
    }

    private async Task<OpcUaMessage> WaitForServiceAsync(uint typeId, uint requestId, string operation, CancellationToken cancellationToken)
    {
        var message = await WaitForMessageAsync("MSG", requestId, operation, cancellationToken, "OPN").ConfigureAwait(false);
        if (message.TypeId != typeId)
        {
            throw new OpcUaException($"OPC UA {operation} 响应类型为 i={message.TypeId}，期望 i={typeId}。");
        }

        return message;
    }

    private async Task<OpcUaMessage> WaitForMessageAsync(
        string expectedType,
        uint requestId,
        string operation,
        CancellationToken cancellationToken,
        string? alternateType = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            Task dataPulse;
            lock (_receive.SyncRoot)
            {
                if (OpcUaCodec.TryTakeMessage(_receive.Bytes, out var message))
                {
                    if (!string.Equals(message.Type, expectedType, StringComparison.Ordinal)
                        && (alternateType is null || !string.Equals(message.Type, alternateType, StringComparison.Ordinal)))
                    {
                        throw new OpcUaException($"OPC UA {operation} 收到 {message.Type}，期望 {expectedType}。");
                    }

                    if (requestId != 0 && message.RequestId != requestId)
                    {
                        throw new OpcUaException($"OPC UA {operation} 的 RequestId 为 {message.RequestId}，期望 {requestId}。");
                    }

                    return message;
                }

                dataPulse = _receive.WaitForDataLocked();
            }

            try
            {
                await dataPulse.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new OpcUaException(
                    OpcUaStatusCodes.BadTimeout,
                    $"通道 {_channel.Name} 在 {_timeout.TotalMilliseconds:0} ms 内未收到 OPC UA {operation} 应答。请检查 TCP 4840、SecurityPolicy None，或用 OpcUaServerResponder 联调。");
            }
        }
    }

    private uint NextRequestId()
    {
        _requestId++;
        if (_requestId == 0)
        {
            _requestId = 1;
        }

        return _requestId;
    }

    private uint NextSequenceNumber()
    {
        _sequenceNumber++;
        if (_sequenceNumber == 0)
        {
            _sequenceNumber = 1;
        }

        return _sequenceNumber;
    }

    private void ResetSession()
    {
        Volatile.Write(ref _sessionActive, 0);
        _authenticationToken = null;
        _channelId = 0;
        _tokenId = 0;
        _receive.Clear();
    }

    private void OnDataReceived(object? sender, ChannelDataReceivedEventArgs e)
        => _receive.Append(e.Data);

    private void OnStateChanged(object? sender, ChannelStateChangedEventArgs e)
    {
        if (e.Current is ChannelState.Faulted or ChannelState.Closed)
        {
            ResetSession();
            _receive.CancelPending(new OpcUaException($"通道 {_channel.Name} 已变为 {e.Current}，未完成的 OPC UA 请求已取消。"));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static OpcUaOptions CopyOptions(OpcUaOptions source)
        => new()
        {
            EndpointUrl = source.EndpointUrl,
            ApplicationUri = source.ApplicationUri,
            ApplicationName = source.ApplicationName,
            SessionName = source.SessionName,
            RequestedSessionTimeout = source.RequestedSessionTimeout,
            Anonymous = source.Anonymous,
            Username = source.Username,
            Password = source.Password,
            AutomaticReconnect = source.AutomaticReconnect
        };
}
