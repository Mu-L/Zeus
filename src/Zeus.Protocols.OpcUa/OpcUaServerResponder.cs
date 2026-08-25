namespace Zeus;

/// <summary>OPC UA 内存虚拟 Server，可直接交给 <c>AddVirtualChannel</c>。</summary>
public sealed class OpcUaServerResponder : IVirtualResponder
{
    private readonly OpcUaServerMemory _memory;
    private readonly string? _username;
    private readonly string? _password;
    private uint _nextChannelId = 1;
    private uint _nextTokenId = 1;
    private uint _nextSessionNumeric = 1000;
    private uint _channelId;
    private uint _tokenId;
    private OpcUaNodeId? _authenticationToken;

    /// <summary>创建虚拟 OPC UA Server。</summary>
    /// <param name="memory">地址空间。省略时预置 ServerName 与 CurrentTime。</param>
    /// <param name="username">可选用户名。省略时接受匿名登录。</param>
    /// <param name="password">可选密码。</param>
    public OpcUaServerResponder(OpcUaServerMemory? memory = null, string? username = null, string? password = null)
    {
        _memory = memory ?? new OpcUaServerMemory();
        _username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        _password = password;
    }

    /// <summary>内存地址空间。</summary>
    public OpcUaServerMemory Memory => _memory;

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> RespondAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
        => Task.FromResult(Respond(request));

    /// <summary>同步处理一帧请求。虚拟 Server 无 I/O，由 <see cref="RespondAsync"/> 转发。</summary>
    private ReadOnlyMemory<byte>? Respond(ReadOnlyMemory<byte> request)
    {
        OpcUaMessage message;
        try
        {
            message = OpcUaCodec.DecodeMessage(request.Span);
        }
        catch (Exception)
        {
            return OpcUaCodec.EncodeError(OpcUaStatusCodes.BadDecodingError, "无法解码 OPC UA 报文。");
        }

        return message.Type switch
        {
            "HEL" => OpcUaCodec.EncodeAcknowledge(),
            "OPN" when message.TypeId == OpcUaCodec.TypeOpenSecureChannelRequest => HandleOpen(message),
            "MSG" => HandleService(message),
            _ => OpcUaCodec.EncodeError(OpcUaStatusCodes.BadServiceUnsupported, $"不支持的报文 {message.Type}。")
        };
    }

    private byte[] HandleOpen(OpcUaMessage message)
    {
        _channelId = _nextChannelId++;
        _tokenId = _nextTokenId++;
        return OpcUaCodec.EncodeOpenSecureChannelResponse(message.RequestId, message.SequenceNumber, _channelId, _tokenId);
    }

    private byte[] HandleService(OpcUaMessage message)
    {
        if (message.ChannelId != 0 && _channelId != 0 && message.ChannelId != _channelId)
        {
            return OpcUaCodec.EncodeError(OpcUaStatusCodes.BadSecureChannelIdInvalid, "安全通道标识无效。");
        }

        return message.TypeId switch
        {
            OpcUaCodec.TypeCreateSessionRequest => HandleCreateSession(message),
            OpcUaCodec.TypeActivateSessionRequest => HandleActivateSession(message),
            OpcUaCodec.TypeReadRequest => HandleRead(message),
            OpcUaCodec.TypeWriteRequest => HandleWrite(message),
            _ => OpcUaCodec.EncodeError(OpcUaStatusCodes.BadServiceUnsupported, $"不支持的服务 i={message.TypeId}。")
        };
    }

    private byte[] HandleCreateSession(OpcUaMessage message)
    {
        _authenticationToken = OpcUaNodeId.Numeric(_nextSessionNumeric++, 1);
        var sessionId = OpcUaNodeId.Numeric(_nextSessionNumeric++, 1);
        return OpcUaCodec.EncodeCreateSessionResponse(_channelId, _tokenId, message.RequestId, message.SequenceNumber, sessionId, _authenticationToken);
    }

    private byte[] HandleActivateSession(OpcUaMessage message)
    {
        if (_authenticationToken is null || message.AuthenticationToken is null || !message.AuthenticationToken.Equals(_authenticationToken))
        {
            return OpcUaCodec.EncodeServiceFault(_channelId, _tokenId, message.RequestId, message.SequenceNumber, OpcUaStatusCodes.BadSessionIdInvalid);
        }

        if (_username is not null
            && (!string.Equals(message.Text, _username, StringComparison.Ordinal) || !string.Equals(message.Password, _password, StringComparison.Ordinal)))
        {
            return OpcUaCodec.EncodeServiceFault(_channelId, _tokenId, message.RequestId, message.SequenceNumber, OpcUaStatusCodes.BadIdentityTokenInvalid);
        }

        return OpcUaCodec.EncodeActivateSessionResponse(_channelId, _tokenId, message.RequestId, message.SequenceNumber);
    }

    private byte[] HandleRead(OpcUaMessage message)
    {
        if (!EnsureSession(message))
        {
            return OpcUaCodec.EncodeServiceFault(_channelId, _tokenId, message.RequestId, message.SequenceNumber, OpcUaStatusCodes.BadSessionIdInvalid);
        }

        var values = new List<OpcUaValue>();
        foreach (var nodeId in message.NodeIds ?? [])
        {
            if (nodeId.ToString() == "i=2258")
            {
                values.Add(OpcUaValue.DateTime(DateTime.UtcNow));
                continue;
            }

            if (_memory.TryGet(nodeId.ToString(), out var value) && value is not null)
            {
                values.Add(value);
            }
            else
            {
                values.Add(new OpcUaValue(OpcUaDataType.String, null, OpcUaStatusCodes.BadNodeIdUnknown));
            }
        }

        return OpcUaCodec.EncodeReadResponse(_channelId, _tokenId, message.RequestId, message.SequenceNumber, values);
    }

    private byte[] HandleWrite(OpcUaMessage message)
    {
        if (!EnsureSession(message))
        {
            return OpcUaCodec.EncodeServiceFault(_channelId, _tokenId, message.RequestId, message.SequenceNumber, OpcUaStatusCodes.BadSessionIdInvalid);
        }

        var results = new List<uint>();
        var nodeIds = message.NodeIds ?? [];
        var values = message.Values ?? [];
        for (var i = 0; i < nodeIds.Count; i++)
        {
            results.Add(i < values.Count ? _memory.TrySet(nodeIds[i].ToString(), values[i]) : OpcUaStatusCodes.BadDecodingError);
        }

        return OpcUaCodec.EncodeWriteResponse(_channelId, _tokenId, message.RequestId, message.SequenceNumber, results);
    }

    private bool EnsureSession(OpcUaMessage message)
        => _authenticationToken is not null
            && message.AuthenticationToken is not null
            && message.AuthenticationToken.Equals(_authenticationToken);
}
