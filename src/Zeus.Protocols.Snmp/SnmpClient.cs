namespace Zeus;

/// <summary>在一条通道上执行 SNMP v2c GET/SET 请求。同一客户端串行发送，适合 UDP、TCP 或虚拟通道。</summary>
public sealed class SnmpClient : IAsyncDisposable
{
    private readonly IChannel _channel;
    private readonly SnmpOptions _options;
    private readonly TimeSpan _timeout;
    private readonly ChannelTransactionCoordinator _gate;
    private readonly ProtocolResponseBuffer _receive;
    private int _requestId;

    /// <summary>创建 SNMP v2c 客户端。</summary>
    public SnmpClient(IChannel channel, SnmpOptions? options = null, TimeSpan? timeout = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _gate = ChannelTransactionCoordinator.For(_channel);
        _receive = new ProtocolResponseBuffer(_channel);
        _options = CopyOptions(options ?? new SnmpOptions());
        ValidateOptions(_options);
        _timeout = timeout ?? TimeSpan.FromSeconds(1);
        _requestId = _options.InitialRequestId <= 0 ? 1 : _options.InitialRequestId - 1;
        _channel.DataReceived += OnDataReceived;
        _channel.StateChanged += OnStateChanged;
    }

    /// <summary>绑定的通道。</summary>
    public IChannel Channel => _channel;

    /// <summary>当前会话选项副本。</summary>
    public SnmpOptions Options => CopyOptions(_options);

    /// <summary>读取一个 OID。</summary>
    public async Task<SnmpValue> GetAsync(string oid, CancellationToken cancellationToken = default)
    {
        var variable = await ExecuteAsync(
            SnmpCodec.GetRequest,
            _options.Community,
            SnmpCodec.NormalizeOid(oid),
            null,
            cancellationToken).ConfigureAwait(false);
        return variable.Value;
    }

    /// <summary>写入一个 OID。</summary>
    public Task SetAsync(string oid, SnmpValue value, CancellationToken cancellationToken = default)
        => ExecuteAsync(
            SnmpCodec.SetRequest,
            _options.WriteCommunity ?? _options.Community,
            SnmpCodec.NormalizeOid(oid),
            value ?? throw new ArgumentNullException(nameof(value)),
            cancellationToken);

    /// <summary>读取整数类值并转换为 <see cref="long"/>。</summary>
    public async Task<long> GetIntegerAsync(string oid, CancellationToken cancellationToken = default)
    {
        var value = await GetAsync(oid, cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(SnmpCodec.ToEngineeringValue(value, null), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>读取 UTF-8 文本。OCTET STRING 会按 UTF-8 解码。</summary>
    public async Task<string> GetTextAsync(string oid, CancellationToken cancellationToken = default)
    {
        var value = await GetAsync(oid, cancellationToken).ConfigureAwait(false);
        return value.DataType switch
        {
            SnmpDataType.Text => Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            SnmpDataType.OctetString when value.Value is byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            _ => Convert.ToString(SnmpCodec.ToEngineeringValue(value, null), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _channel.DataReceived -= OnDataReceived;
        _channel.StateChanged -= OnStateChanged;
        return ValueTask.CompletedTask;
    }

    private async Task<SnmpVariable> ExecuteAsync(
        byte pduType,
        string community,
        string oid,
        SnmpValue? value,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_receive.SyncRoot)
            {
                _receive.Bytes.Clear();
            }

            var requestId = NextRequestId();
            var packet = pduType == SnmpCodec.SetRequest
                ? SnmpCodec.EncodeSetRequest(community, requestId, oid, value!)
                : SnmpCodec.EncodeGetRequest(community, requestId, oid);
            await _channel.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
            var response = await WaitForResponseAsync(requestId, cancellationToken).ConfigureAwait(false);
            if (response.ErrorStatus != SnmpErrorStatus.NoError)
            {
                throw new SnmpException(response.ErrorStatus, response.ErrorIndex);
            }

            if (response.Variables.Count == 0)
            {
                throw new ZeusProtocolException("SNMP 响应缺少 varbind。");
            }

            var variable = response.Variables[0];
            if (!string.Equals(variable.Oid, oid, StringComparison.Ordinal))
            {
                throw new ZeusProtocolException($"SNMP 响应 OID 为 {variable.Oid}，期望 {oid}。");
            }

            return variable;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SnmpMessage> WaitForResponseAsync(int requestId, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        while (true)
        {
            ProtocolTimeout.ThrowIfCancellationRequested(
                timeoutCts.Token,
                cancellationToken,
                () => new ZeusProtocolException(
                    $"通道 {_channel.Name} 在 {_timeout.TotalMilliseconds:0} ms 内未收到 SNMP 应答。请检查 UDP 161、community 或用 SnmpAgentResponder 联调。"));
            Task dataPulse;
            lock (_receive.SyncRoot)
            {
                if (_receive.Bytes.Count > 0)
                {
                    var packet = _receive.Bytes.ToArray();
                    _receive.Bytes.Clear();
                    var response = SnmpCodec.DecodeMessage(packet);
                    if (response.PduType != SnmpCodec.GetResponse)
                    {
                        throw new ZeusProtocolException($"SNMP 响应 PDU 类型 0x{response.PduType:X2} 异常。");
                    }

                    if (response.RequestId != requestId)
                    {
                        throw new ZeusProtocolException($"SNMP 响应 request-id 为 {response.RequestId}，期望 {requestId}。请避免多路并发共用同一通道。 ");
                    }

                    return response;
                }

                dataPulse = _receive.WaitForDataLocked();
            }

            try
            {
                await dataPulse.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ZeusProtocolException(
                    $"通道 {_channel.Name} 在 {_timeout.TotalMilliseconds:0} ms 内未收到 SNMP 应答。请检查 UDP 161、community 或用 SnmpAgentResponder 联调。");
            }
        }
    }

    private int NextRequestId()
    {
        _requestId++;
        if (_requestId <= 0)
        {
            _requestId = 1;
        }

        return _requestId;
    }

    private void OnDataReceived(object? sender, ChannelDataReceivedEventArgs e)
        => _receive.Append(e.Data);

    private void OnStateChanged(object? sender, ChannelStateChangedEventArgs e)
    {
        if (e.Current is ChannelState.Faulted or ChannelState.Closed)
        {
            _receive.CancelPending(new ZeusProtocolException(
                $"通道 {_channel.Name} 已变为 {e.Current}，未完成的 SNMP 请求已取消。"));
        }
    }
    private static SnmpOptions CopyOptions(SnmpOptions source)
        => new()
        {
            Community = source.Community,
            WriteCommunity = source.WriteCommunity,
            InitialRequestId = source.InitialRequestId
        };

    private static void ValidateOptions(SnmpOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Community))
        {
            throw new ZeusProtocolException("SNMP community 不能为空。");
        }

        if (options.WriteCommunity is not null && string.IsNullOrWhiteSpace(options.WriteCommunity))
        {
            throw new ZeusProtocolException("SNMP write community 不能是空字符串。");
        }
    }
}
