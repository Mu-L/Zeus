using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Zeus;

/// <summary>
/// OPC UA Binary 会话与服务报文编解码。
/// 当前只覆盖 opc.tcp、SecurityPolicy None、单块报文，以及会话/Value 读写。
/// </summary>
internal static class OpcUaCodec
{
    public const uint AttributeValue = 13;
    public const uint SecurityModeNone = 1;
    public const uint ApplicationTypeClient = 1;
    public const uint TimestampsToReturnNeither = 3;
    public const uint OpenSecureChannelIssue = 0;

    public const uint TypeOpenSecureChannelRequest = 446;
    public const uint TypeOpenSecureChannelResponse = 449;
    public const uint TypeCloseSecureChannelRequest = 452;
    public const uint TypeCloseSecureChannelResponse = 455;
    public const uint TypeServiceFault = 397;
    public const uint TypeCreateSessionRequest = 461;
    public const uint TypeCreateSessionResponse = 464;
    public const uint TypeActivateSessionRequest = 467;
    public const uint TypeActivateSessionResponse = 470;
    public const uint TypeCloseSessionRequest = 473;
    public const uint TypeCloseSessionResponse = 476;
    public const uint TypeReadRequest = 631;
    public const uint TypeReadResponse = 634;
    public const uint TypeWriteRequest = 673;
    public const uint TypeWriteResponse = 676;
    public const uint TypeAnonymousIdentityToken = 321;
    public const uint TypeUserNameIdentityToken = 324;

    public const string SecurityPolicyNone = "http://opcfoundation.org/UA/SecurityPolicy#None";
    public const string DefaultEndpointUrl = "opc.tcp://localhost:4840";

    public static bool IsNumeric(OpcUaDataType dataType)
        => dataType is OpcUaDataType.SByte or OpcUaDataType.Byte or OpcUaDataType.Int16 or OpcUaDataType.UInt16
            or OpcUaDataType.Int32 or OpcUaDataType.UInt32 or OpcUaDataType.Int64 or OpcUaDataType.UInt64
            or OpcUaDataType.Float or OpcUaDataType.Double;

    public static object ToEngineeringValue(OpcUaValue value, double? scale)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!OpcUaStatusCodes.IsGood(value.StatusCode))
        {
            throw new OpcUaException(value.StatusCode, $"OPC UA 状态码 0x{value.StatusCode:X8}。");
        }

        if (scale is null || !IsNumeric(value.DataType))
        {
            return value.Value ?? string.Empty;
        }

        return Convert.ToDouble(value.Value, CultureInfo.InvariantCulture) * scale.Value;
    }

    public static OpcUaValue FromEngineeringValue(OpcUaDataType dataType, object value, double? scale)
    {
        if (scale is null || !IsNumeric(dataType))
        {
            return OpcUaValue.FromObject(dataType, value);
        }

        var raw = Convert.ToDouble(value, CultureInfo.InvariantCulture) / scale.Value;
        return dataType switch
        {
            OpcUaDataType.Float => OpcUaValue.Float((float)raw),
            OpcUaDataType.Double => OpcUaValue.Double(raw),
            _ => OpcUaValue.FromObject(dataType, Convert.ChangeType(Math.Round(raw), ToClrType(dataType), CultureInfo.InvariantCulture))
        };
    }

    public static OpcUaValue Coerce(OpcUaValue value, OpcUaDataType expected)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!OpcUaStatusCodes.IsGood(value.StatusCode))
        {
            return value;
        }

        if (value.DataType == expected)
        {
            return value;
        }

        return OpcUaValue.FromObject(expected, value.Value ?? string.Empty) with
        {
            StatusCode = value.StatusCode,
            SourceTimestamp = value.SourceTimestamp
        };
    }

    public static byte[] EncodeHello(string endpointUrl)
    {
        var body = new List<byte>();
        OpcUaBinary.WriteUInt32(body, 0);
        OpcUaBinary.WriteUInt32(body, 65535);
        OpcUaBinary.WriteUInt32(body, 65535);
        OpcUaBinary.WriteUInt32(body, 0);
        OpcUaBinary.WriteUInt32(body, 0);
        OpcUaBinary.WriteString(body, string.IsNullOrWhiteSpace(endpointUrl) ? DefaultEndpointUrl : endpointUrl.Trim());
        return WrapTransport("HEL", body);
    }

    public static byte[] EncodeAcknowledge()
    {
        var body = new List<byte>();
        OpcUaBinary.WriteUInt32(body, 0);
        OpcUaBinary.WriteUInt32(body, 65535);
        OpcUaBinary.WriteUInt32(body, 65535);
        OpcUaBinary.WriteUInt32(body, 0);
        OpcUaBinary.WriteUInt32(body, 0);
        return WrapTransport("ACK", body);
    }

    public static byte[] EncodeError(uint statusCode, string reason)
    {
        var body = new List<byte>();
        OpcUaBinary.WriteUInt32(body, statusCode);
        OpcUaBinary.WriteString(body, reason);
        return WrapTransport("ERR", body);
    }

    public static byte[] EncodeOpenSecureChannelRequest(uint requestId, uint sequenceNumber)
    {
        var body = new List<byte>();
        WriteTypeId(body, TypeOpenSecureChannelRequest);
        WriteRequestHeader(body, null, requestId);
        OpcUaBinary.WriteUInt32(body, 0);
        OpcUaBinary.WriteUInt32(body, OpenSecureChannelIssue);
        OpcUaBinary.WriteUInt32(body, SecurityModeNone);
        OpcUaBinary.WriteByteString(body, []);
        OpcUaBinary.WriteUInt32(body, 600_000);
        return WrapSecure("OPN", 0, null, sequenceNumber, requestId, body);
    }

    public static byte[] EncodeOpenSecureChannelResponse(uint requestId, uint sequenceNumber, uint channelId, uint tokenId)
    {
        var body = new List<byte>();
        WriteTypeId(body, TypeOpenSecureChannelResponse);
        WriteResponseHeader(body, requestId, OpcUaStatusCodes.Good);
        OpcUaBinary.WriteUInt32(body, 0);
        OpcUaBinary.WriteUInt32(body, channelId);
        OpcUaBinary.WriteUInt32(body, tokenId);
        OpcUaBinary.WriteDateTime(body, DateTime.UtcNow);
        OpcUaBinary.WriteUInt32(body, 600_000);
        OpcUaBinary.WriteByteString(body, []);
        return WrapSecure("OPN", channelId, null, sequenceNumber, requestId, body);
    }

    public static byte[] EncodeCreateSessionRequest(uint channelId, uint tokenId, uint requestId, uint sequenceNumber, OpcUaOptions options, string fallbackName)
    {
        var body = new List<byte>();
        WriteTypeId(body, TypeCreateSessionRequest);
        WriteRequestHeader(body, null, requestId);
        OpcUaBinary.WriteString(body, options.ApplicationUri ?? "urn:zeus:" + fallbackName);
        OpcUaBinary.WriteString(body, "urn:zeus");
        OpcUaBinary.WriteLocalizedText(body, options.ApplicationName ?? fallbackName);
        OpcUaBinary.WriteUInt32(body, ApplicationTypeClient);
        OpcUaBinary.WriteString(body, null);
        OpcUaBinary.WriteString(body, null);
        OpcUaBinary.WriteInt32(body, 0);
        OpcUaBinary.WriteString(body, null);
        OpcUaBinary.WriteString(body, string.IsNullOrWhiteSpace(options.EndpointUrl) ? DefaultEndpointUrl : options.EndpointUrl.Trim());
        OpcUaBinary.WriteString(body, options.SessionName ?? fallbackName);
        OpcUaBinary.WriteByteString(body, []);
        OpcUaBinary.WriteByteString(body, null);
        OpcUaBinary.WriteDouble(body, options.RequestedSessionTimeout);
        OpcUaBinary.WriteUInt32(body, 0);
        return WrapSecure("MSG", channelId, tokenId, sequenceNumber, requestId, body);
    }

    public static byte[] EncodeCreateSessionResponse(uint channelId, uint tokenId, uint requestId, uint sequenceNumber, OpcUaNodeId sessionId, OpcUaNodeId authenticationToken)
    {
        var body = new List<byte>();
        WriteTypeId(body, TypeCreateSessionResponse);
        WriteResponseHeader(body, requestId, OpcUaStatusCodes.Good);
        OpcUaBinary.WriteNodeId(body, sessionId);
        OpcUaBinary.WriteNodeId(body, authenticationToken);
        OpcUaBinary.WriteDouble(body, 60_000);
        OpcUaBinary.WriteByteString(body, []);
        OpcUaBinary.WriteByteString(body, null);
        OpcUaBinary.WriteInt32(body, 0);
        OpcUaBinary.WriteInt32(body, 0);
        OpcUaBinary.WriteSignatureData(body);
        OpcUaBinary.WriteUInt32(body, 0);
        return WrapSecure("MSG", channelId, tokenId, sequenceNumber, requestId, body);
    }

    public static byte[] EncodeActivateSessionRequest(
        uint channelId,
        uint tokenId,
        uint requestId,
        uint sequenceNumber,
        OpcUaNodeId authenticationToken,
        OpcUaOptions options)
    {
        var body = new List<byte>();
        WriteTypeId(body, TypeActivateSessionRequest);
        WriteRequestHeader(body, authenticationToken, requestId);
        OpcUaBinary.WriteSignatureData(body);
        OpcUaBinary.WriteInt32(body, 0);
        OpcUaBinary.WriteInt32(body, 0);
        WriteUserIdentityToken(body, options);
        OpcUaBinary.WriteSignatureData(body);
        return WrapSecure("MSG", channelId, tokenId, sequenceNumber, requestId, body);
    }

    public static byte[] EncodeActivateSessionResponse(uint channelId, uint tokenId, uint requestId, uint sequenceNumber)
    {
        var body = new List<byte>();
        WriteTypeId(body, TypeActivateSessionResponse);
        WriteResponseHeader(body, requestId, OpcUaStatusCodes.Good);
        OpcUaBinary.WriteByteString(body, []);
        OpcUaBinary.WriteInt32(body, 0);
        OpcUaBinary.WriteInt32(body, 0);
        return WrapSecure("MSG", channelId, tokenId, sequenceNumber, requestId, body);
    }

    public static byte[] EncodeServiceFault(uint channelId, uint? tokenId, uint requestId, uint sequenceNumber, uint statusCode)
    {
        var body = new List<byte>();
        WriteTypeId(body, TypeServiceFault);
        WriteResponseHeader(body, requestId, statusCode);
        return WrapSecure("MSG", channelId, tokenId, sequenceNumber, requestId, body);
    }

    public static byte[] EncodeReadRequest(
        uint channelId,
        uint tokenId,
        uint requestId,
        uint sequenceNumber,
        OpcUaNodeId authenticationToken,
        IReadOnlyList<OpcUaNodeId> nodeIds)
    {
        var body = new List<byte>();
        WriteTypeId(body, TypeReadRequest);
        WriteRequestHeader(body, authenticationToken, requestId);
        OpcUaBinary.WriteDouble(body, 0);
        OpcUaBinary.WriteUInt32(body, TimestampsToReturnNeither);
        OpcUaBinary.WriteInt32(body, nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            OpcUaBinary.WriteNodeId(body, nodeId);
            OpcUaBinary.WriteUInt32(body, AttributeValue);
            OpcUaBinary.WriteString(body, null);
            OpcUaBinary.WriteQualifiedName(body, 0, null);
        }

        return WrapSecure("MSG", channelId, tokenId, sequenceNumber, requestId, body);
    }

    public static byte[] EncodeReadResponse(
        uint channelId,
        uint tokenId,
        uint requestId,
        uint sequenceNumber,
        IReadOnlyList<OpcUaValue> values)
    {
        var body = new List<byte>();
        WriteTypeId(body, TypeReadResponse);
        WriteResponseHeader(body, requestId, OpcUaStatusCodes.Good);
        OpcUaBinary.WriteInt32(body, values.Count);
        foreach (var value in values)
        {
            OpcUaBinary.WriteDataValue(body, value);
        }

        OpcUaBinary.WriteInt32(body, 0);
        return WrapSecure("MSG", channelId, tokenId, sequenceNumber, requestId, body);
    }

    public static byte[] EncodeWriteRequest(
        uint channelId,
        uint tokenId,
        uint requestId,
        uint sequenceNumber,
        OpcUaNodeId authenticationToken,
        IReadOnlyList<(OpcUaNodeId NodeId, OpcUaValue Value)> items)
    {
        var body = new List<byte>();
        WriteTypeId(body, TypeWriteRequest);
        WriteRequestHeader(body, authenticationToken, requestId);
        OpcUaBinary.WriteInt32(body, items.Count);
        foreach (var item in items)
        {
            OpcUaBinary.WriteNodeId(body, item.NodeId);
            OpcUaBinary.WriteUInt32(body, AttributeValue);
            OpcUaBinary.WriteString(body, null);
            OpcUaBinary.WriteDataValue(body, item.Value);
        }

        return WrapSecure("MSG", channelId, tokenId, sequenceNumber, requestId, body);
    }

    public static byte[] EncodeWriteResponse(
        uint channelId,
        uint tokenId,
        uint requestId,
        uint sequenceNumber,
        IReadOnlyList<uint> results)
    {
        var body = new List<byte>();
        WriteTypeId(body, TypeWriteResponse);
        WriteResponseHeader(body, requestId, OpcUaStatusCodes.Good);
        OpcUaBinary.WriteInt32(body, results.Count);
        foreach (var result in results)
        {
            OpcUaBinary.WriteUInt32(body, result);
        }

        OpcUaBinary.WriteInt32(body, 0);
        return WrapSecure("MSG", channelId, tokenId, sequenceNumber, requestId, body);
    }

    public static bool TryTakeMessage(List<byte> buffer, out OpcUaMessage message)
    {
        message = default!;
        if (buffer.Count < 8)
        {
            return false;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.ToArray().AsSpan(4, 4));
        if (size < 8 || size > ProtocolReceiveBuffer.DefaultMaxBytes)
        {
            throw new OpcUaException($"OPC UA 报文长度 {size} 无效。");
        }

        if (buffer.Count < size)
        {
            return false;
        }

        var packet = buffer.Take((int)size).ToArray();
        buffer.RemoveRange(0, (int)size);
        message = DecodeMessage(packet);
        return true;
    }

    public static OpcUaMessage DecodeMessage(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8)
        {
            throw new OpcUaException("OPC UA 报文过短。");
        }

        var type = Encoding.ASCII.GetString(packet[..3]);
        var final = (char)packet[3];
        var size = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4));
        if (size != packet.Length)
        {
            throw new OpcUaException($"OPC UA 报文长度字段为 {size}，实际 {packet.Length}。");
        }

        if (final is not ('F' or 'C' or 'A'))
        {
            throw new OpcUaException($"OPC UA 分块标志 {(int)final} 无效。");
        }

        if (final != 'F')
        {
            throw new OpcUaException("当前不支持 OPC UA 多分块报文。");
        }

        var payload = packet[8..];
        return type switch
        {
            "HEL" => DecodeHello(payload),
            "ACK" => DecodeAcknowledge(payload),
            "ERR" => DecodeError(payload),
            "OPN" or "MSG" or "CLO" => DecodeSecure(type, payload),
            _ => throw new OpcUaException($"不支持的 OPC UA 报文类型 {type}。")
        };
    }

    public static void ValidateOptions(OpcUaOptions options)
    {
        if (options.RequestedSessionTimeout <= 0)
        {
            throw new OpcUaException("OPC UA 会话超时必须大于 0。");
        }

        if (!options.Anonymous && string.IsNullOrWhiteSpace(options.Username))
        {
            throw new OpcUaException("非匿名登录必须提供用户名。");
        }
    }

    private static OpcUaMessage DecodeHello(ReadOnlySpan<byte> payload)
    {
        var reader = new OpcUaBinary.Reader(payload);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        var endpointUrl = OpcUaBinary.ReadString(ref reader);
        return new OpcUaMessage("HEL", 0, 0, 0, 0, 0, endpointUrl, null, null, null);
    }

    private static OpcUaMessage DecodeAcknowledge(ReadOnlySpan<byte> payload)
    {
        var reader = new OpcUaBinary.Reader(payload);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        return new OpcUaMessage("ACK", 0, 0, 0, 0, 0, null, null, null, null);
    }

    private static OpcUaMessage DecodeError(ReadOnlySpan<byte> payload)
    {
        var reader = new OpcUaBinary.Reader(payload);
        var status = OpcUaBinary.ReadUInt32(ref reader);
        var reason = OpcUaBinary.ReadString(ref reader);
        throw new OpcUaException(status, $"OPC UA 对端返回错误：{reason ?? status.ToString("X8", CultureInfo.InvariantCulture)}。");
    }

    private static OpcUaMessage DecodeSecure(string type, ReadOnlySpan<byte> payload)
    {
        var reader = new OpcUaBinary.Reader(payload);
        var channelId = OpcUaBinary.ReadUInt32(ref reader);
        uint? tokenId = null;
        if (type == "OPN")
        {
            var policy = OpcUaBinary.ReadString(ref reader);
            _ = OpcUaBinary.ReadByteString(ref reader);
            _ = OpcUaBinary.ReadByteString(ref reader);
            if (!string.IsNullOrEmpty(policy) && !string.Equals(policy, SecurityPolicyNone, StringComparison.Ordinal))
            {
                throw new OpcUaException(OpcUaStatusCodes.BadSecurityPolicyRejected, $"当前只支持 SecurityPolicy None，对端策略为 {policy}。");
            }
        }
        else
        {
            tokenId = OpcUaBinary.ReadUInt32(ref reader);
        }

        var sequenceNumber = OpcUaBinary.ReadUInt32(ref reader);
        var requestId = OpcUaBinary.ReadUInt32(ref reader);
        var typeId = ReadTypeId(ref reader);
        return DecodeService(type, channelId, tokenId, sequenceNumber, requestId, typeId, ref reader);
    }

    private static OpcUaMessage DecodeService(
        string type,
        uint channelId,
        uint? tokenId,
        uint sequenceNumber,
        uint requestId,
        uint typeId,
        ref OpcUaBinary.Reader reader)
    {
        return typeId switch
        {
            TypeOpenSecureChannelRequest => DecodeOpenSecureChannelRequest(type, channelId, tokenId, sequenceNumber, requestId, ref reader),
            TypeOpenSecureChannelResponse => DecodeOpenSecureChannelResponse(type, channelId, tokenId, sequenceNumber, requestId, ref reader),
            TypeServiceFault => DecodeServiceFault(type, channelId, tokenId, sequenceNumber, requestId, ref reader),
            TypeCreateSessionRequest => DecodeCreateSessionRequest(type, channelId, tokenId, sequenceNumber, requestId, ref reader),
            TypeCreateSessionResponse => DecodeCreateSessionResponse(type, channelId, tokenId, sequenceNumber, requestId, ref reader),
            TypeActivateSessionRequest => DecodeActivateSessionRequest(type, channelId, tokenId, sequenceNumber, requestId, ref reader),
            TypeActivateSessionResponse => DecodeSimpleResponse(type, channelId, tokenId, sequenceNumber, requestId, typeId, ref reader),
            TypeReadRequest => DecodeReadRequest(type, channelId, tokenId, sequenceNumber, requestId, ref reader),
            TypeReadResponse => DecodeReadResponse(type, channelId, tokenId, sequenceNumber, requestId, ref reader),
            TypeWriteRequest => DecodeWriteRequest(type, channelId, tokenId, sequenceNumber, requestId, ref reader),
            TypeWriteResponse => DecodeWriteResponse(type, channelId, tokenId, sequenceNumber, requestId, ref reader),
            TypeCloseSessionRequest or TypeCloseSessionResponse or TypeCloseSecureChannelRequest or TypeCloseSecureChannelResponse
                => DecodeSimpleResponse(type, channelId, tokenId, sequenceNumber, requestId, typeId, ref reader),
            _ => throw new OpcUaException(OpcUaStatusCodes.BadServiceUnsupported, $"不支持的 OPC UA 服务类型 i={typeId}。")
        };
    }

    private static OpcUaMessage DecodeServiceFault(string type, uint channelId, uint? tokenId, uint sequenceNumber, uint requestId, ref OpcUaBinary.Reader reader)
    {
        EnsureGoodResponse(ref reader);
        return new OpcUaMessage(type, channelId, tokenId, sequenceNumber, requestId, TypeServiceFault, null, null, null, null);
    }

    private static OpcUaMessage DecodeOpenSecureChannelRequest(string type, uint channelId, uint? tokenId, uint sequenceNumber, uint requestId, ref OpcUaBinary.Reader reader)
    {
        SkipRequestHeader(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        var securityMode = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadByteString(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        if (securityMode != SecurityModeNone)
        {
            throw new OpcUaException(OpcUaStatusCodes.BadSecurityPolicyRejected, "当前只支持 MessageSecurityMode None。");
        }

        return new OpcUaMessage(type, channelId, tokenId, sequenceNumber, requestId, TypeOpenSecureChannelRequest, null, null, null, null);
    }

    private static OpcUaMessage DecodeOpenSecureChannelResponse(string type, uint channelId, uint? tokenId, uint sequenceNumber, uint requestId, ref OpcUaBinary.Reader reader)
    {
        EnsureGoodResponse(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        var revisedChannelId = OpcUaBinary.ReadUInt32(ref reader);
        var revisedTokenId = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadDateTime(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadByteString(ref reader);
        return new OpcUaMessage(type, revisedChannelId, revisedTokenId, sequenceNumber, requestId, TypeOpenSecureChannelResponse, null, null, null, null);
    }

    private static OpcUaMessage DecodeCreateSessionRequest(string type, uint channelId, uint? tokenId, uint sequenceNumber, uint requestId, ref OpcUaBinary.Reader reader)
    {
        SkipRequestHeader(ref reader);
        _ = OpcUaBinary.ReadString(ref reader);
        _ = OpcUaBinary.ReadString(ref reader);
        OpcUaBinary.SkipLocalizedText(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadString(ref reader);
        _ = OpcUaBinary.ReadString(ref reader);
        var discoveryCount = OpcUaBinary.ReadArrayLength(ref reader);
        for (var i = 0; i < discoveryCount; i++)
        {
            _ = OpcUaBinary.ReadString(ref reader);
        }

        _ = OpcUaBinary.ReadString(ref reader);
        _ = OpcUaBinary.ReadString(ref reader);
        var sessionName = OpcUaBinary.ReadString(ref reader);
        _ = OpcUaBinary.ReadByteString(ref reader);
        _ = OpcUaBinary.ReadByteString(ref reader);
        _ = OpcUaBinary.ReadDouble(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        return new OpcUaMessage(type, channelId, tokenId, sequenceNumber, requestId, TypeCreateSessionRequest, sessionName, null, null, null);
    }

    private static OpcUaMessage DecodeCreateSessionResponse(string type, uint channelId, uint? tokenId, uint sequenceNumber, uint requestId, ref OpcUaBinary.Reader reader)
    {
        EnsureGoodResponse(ref reader);
        var sessionId = OpcUaBinary.ReadNodeId(ref reader);
        var authenticationToken = OpcUaBinary.ReadNodeId(ref reader);
        return new OpcUaMessage(type, channelId, tokenId, sequenceNumber, requestId, TypeCreateSessionResponse, null, authenticationToken, null, null)
        {
            SessionId = sessionId
        };
    }

    private static OpcUaMessage DecodeActivateSessionRequest(string type, uint channelId, uint? tokenId, uint sequenceNumber, uint requestId, ref OpcUaBinary.Reader reader)
    {
        var authenticationToken = SkipRequestHeader(ref reader);
        OpcUaBinary.SkipSignatureData(ref reader);
        var certificateCount = OpcUaBinary.ReadArrayLength(ref reader);
        for (var i = 0; i < certificateCount; i++)
        {
            _ = OpcUaBinary.ReadByteString(ref reader);
            OpcUaBinary.SkipSignatureData(ref reader);
        }

        var localeCount = OpcUaBinary.ReadArrayLength(ref reader);
        for (var i = 0; i < localeCount; i++)
        {
            _ = OpcUaBinary.ReadString(ref reader);
        }

        var identity = ReadUserIdentity(ref reader);
        return new OpcUaMessage(type, channelId, tokenId, sequenceNumber, requestId, TypeActivateSessionRequest, identity.Username, authenticationToken, null, null)
        {
            Password = identity.Password
        };
    }

    private static OpcUaMessage DecodeReadRequest(string type, uint channelId, uint? tokenId, uint sequenceNumber, uint requestId, ref OpcUaBinary.Reader reader)
    {
        var authenticationToken = SkipRequestHeader(ref reader);
        _ = OpcUaBinary.ReadDouble(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        var count = OpcUaBinary.ReadArrayLength(ref reader);
        var nodeIds = new List<OpcUaNodeId>(count);
        for (var i = 0; i < count; i++)
        {
            nodeIds.Add(OpcUaBinary.ReadNodeId(ref reader));
            _ = OpcUaBinary.ReadUInt32(ref reader);
            _ = OpcUaBinary.ReadString(ref reader);
            OpcUaBinary.SkipQualifiedName(ref reader);
        }

        return new OpcUaMessage(type, channelId, tokenId, sequenceNumber, requestId, TypeReadRequest, null, authenticationToken, nodeIds, null);
    }

    private static OpcUaMessage DecodeReadResponse(string type, uint channelId, uint? tokenId, uint sequenceNumber, uint requestId, ref OpcUaBinary.Reader reader)
    {
        EnsureGoodResponse(ref reader);
        var count = OpcUaBinary.ReadArrayLength(ref reader);
        var values = new List<OpcUaValue>(count);
        for (var i = 0; i < count; i++)
        {
            values.Add(OpcUaBinary.ReadDataValue(ref reader));
        }

        return new OpcUaMessage(type, channelId, tokenId, sequenceNumber, requestId, TypeReadResponse, null, null, null, values);
    }

    private static OpcUaMessage DecodeWriteRequest(string type, uint channelId, uint? tokenId, uint sequenceNumber, uint requestId, ref OpcUaBinary.Reader reader)
    {
        var authenticationToken = SkipRequestHeader(ref reader);
        var count = OpcUaBinary.ReadArrayLength(ref reader);
        var nodeIds = new List<OpcUaNodeId>(count);
        var values = new List<OpcUaValue>(count);
        for (var i = 0; i < count; i++)
        {
            nodeIds.Add(OpcUaBinary.ReadNodeId(ref reader));
            _ = OpcUaBinary.ReadUInt32(ref reader);
            _ = OpcUaBinary.ReadString(ref reader);
            values.Add(OpcUaBinary.ReadDataValue(ref reader));
        }

        return new OpcUaMessage(type, channelId, tokenId, sequenceNumber, requestId, TypeWriteRequest, null, authenticationToken, nodeIds, values);
    }

    private static OpcUaMessage DecodeWriteResponse(string type, uint channelId, uint? tokenId, uint sequenceNumber, uint requestId, ref OpcUaBinary.Reader reader)
    {
        EnsureGoodResponse(ref reader);
        var count = OpcUaBinary.ReadArrayLength(ref reader);
        var values = new List<OpcUaValue>(count);
        for (var i = 0; i < count; i++)
        {
            var status = OpcUaBinary.ReadUInt32(ref reader);
            values.Add(new OpcUaValue(OpcUaDataType.UInt32, status, status));
        }

        return new OpcUaMessage(type, channelId, tokenId, sequenceNumber, requestId, TypeWriteResponse, null, null, null, values);
    }

    private static OpcUaMessage DecodeSimpleResponse(string type, uint channelId, uint? tokenId, uint sequenceNumber, uint requestId, uint typeId, ref OpcUaBinary.Reader reader)
    {
        if (typeId is TypeActivateSessionRequest or TypeCloseSessionRequest or TypeCloseSecureChannelRequest)
        {
            SkipRequestHeader(ref reader);
        }
        else
        {
            EnsureGoodResponse(ref reader);
        }

        return new OpcUaMessage(type, channelId, tokenId, sequenceNumber, requestId, typeId, null, null, null, null);
    }

    private static void WriteUserIdentityToken(List<byte> buffer, OpcUaOptions options)
    {
        if (options.Anonymous || string.IsNullOrWhiteSpace(options.Username))
        {
            var token = new List<byte>();
            OpcUaBinary.WriteString(token, "anonymous");
            OpcUaBinary.WriteExtensionObject(buffer, OpcUaNodeId.Numeric(TypeAnonymousIdentityToken), token.ToArray());
            return;
        }

        var user = new List<byte>();
        OpcUaBinary.WriteString(user, "username");
        OpcUaBinary.WriteString(user, options.Username);
        OpcUaBinary.WriteByteString(user, Encoding.UTF8.GetBytes(options.Password ?? string.Empty));
        OpcUaBinary.WriteString(user, null);
        OpcUaBinary.WriteExtensionObject(buffer, OpcUaNodeId.Numeric(TypeUserNameIdentityToken), user.ToArray());
    }

    private static (string? Username, string? Password) ReadUserIdentity(ref OpcUaBinary.Reader reader)
    {
        var typeId = OpcUaBinary.ReadNodeId(ref reader);
        var encoding = reader.ReadByte();
        if (encoding == 0)
        {
            return (null, null);
        }

        var body = OpcUaBinary.ReadByteString(ref reader) ?? [];
        var inner = new OpcUaBinary.Reader(body);
        if (typeId.IdentifierType == OpcUaNodeIdType.Numeric && Convert.ToUInt32(typeId.Identifier, CultureInfo.InvariantCulture) == TypeUserNameIdentityToken)
        {
            _ = OpcUaBinary.ReadString(ref inner);
            var username = OpcUaBinary.ReadString(ref inner);
            var passwordBytes = OpcUaBinary.ReadByteString(ref inner);
            return (username, passwordBytes is null ? null : Encoding.UTF8.GetString(passwordBytes));
        }

        return (null, null);
    }

    private static void WriteTypeId(List<byte> buffer, uint numericId)
        => OpcUaBinary.WriteNodeId(buffer, OpcUaNodeId.Numeric(numericId));

    private static uint ReadTypeId(ref OpcUaBinary.Reader reader)
    {
        var nodeId = OpcUaBinary.ReadNodeId(ref reader);
        if (nodeId.IdentifierType != OpcUaNodeIdType.Numeric)
        {
            throw new OpcUaException("OPC UA 服务 TypeId 必须是数值 NodeId。");
        }

        return Convert.ToUInt32(nodeId.Identifier, CultureInfo.InvariantCulture);
    }

    private static void WriteRequestHeader(List<byte> buffer, OpcUaNodeId? authenticationToken, uint requestHandle)
    {
        OpcUaBinary.WriteNodeId(buffer, authenticationToken);
        OpcUaBinary.WriteDateTime(buffer, DateTime.UtcNow);
        OpcUaBinary.WriteUInt32(buffer, requestHandle);
        OpcUaBinary.WriteUInt32(buffer, 0);
        OpcUaBinary.WriteString(buffer, null);
        OpcUaBinary.WriteUInt32(buffer, 0);
        OpcUaBinary.WriteNullExtensionObject(buffer);
    }

    private static void WriteResponseHeader(List<byte> buffer, uint requestHandle, uint statusCode)
    {
        OpcUaBinary.WriteDateTime(buffer, DateTime.UtcNow);
        OpcUaBinary.WriteUInt32(buffer, requestHandle);
        OpcUaBinary.WriteUInt32(buffer, statusCode);
        OpcUaBinary.WriteDiagnosticInfo(buffer);
        OpcUaBinary.WriteInt32(buffer, 0);
        OpcUaBinary.WriteNullExtensionObject(buffer);
    }

    private static OpcUaNodeId SkipRequestHeader(ref OpcUaBinary.Reader reader)
    {
        var token = OpcUaBinary.ReadNodeId(ref reader);
        _ = OpcUaBinary.ReadDateTime(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        _ = OpcUaBinary.ReadString(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        OpcUaBinary.SkipExtensionObject(ref reader);
        return token;
    }

    private static void EnsureGoodResponse(ref OpcUaBinary.Reader reader)
    {
        _ = OpcUaBinary.ReadDateTime(ref reader);
        _ = OpcUaBinary.ReadUInt32(ref reader);
        var status = OpcUaBinary.ReadUInt32(ref reader);
        OpcUaBinary.SkipDiagnosticInfo(ref reader);
        var stringCount = OpcUaBinary.ReadArrayLength(ref reader);
        for (var i = 0; i < stringCount; i++)
        {
            _ = OpcUaBinary.ReadString(ref reader);
        }

        OpcUaBinary.SkipExtensionObject(ref reader);
        if (!OpcUaStatusCodes.IsGood(status))
        {
            throw new OpcUaException(status, $"OPC UA 服务失败，状态码 0x{status:X8}。");
        }
    }

    private static byte[] WrapTransport(string type, List<byte> body)
    {
        var packet = new byte[8 + body.Count];
        Encoding.ASCII.GetBytes(type).CopyTo(packet, 0);
        packet[3] = (byte)'F';
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), (uint)packet.Length);
        body.CopyTo(packet, 8);
        return packet;
    }

    private static byte[] WrapSecure(string type, uint channelId, uint? tokenId, uint sequenceNumber, uint requestId, List<byte> body)
    {
        var header = new List<byte>();
        OpcUaBinary.WriteUInt32(header, channelId);
        if (type == "OPN")
        {
            OpcUaBinary.WriteString(header, SecurityPolicyNone);
            OpcUaBinary.WriteByteString(header, null);
            OpcUaBinary.WriteByteString(header, null);
        }
        else
        {
            OpcUaBinary.WriteUInt32(header, tokenId ?? 0);
        }

        OpcUaBinary.WriteUInt32(header, sequenceNumber);
        OpcUaBinary.WriteUInt32(header, requestId);
        header.AddRange(body);
        return WrapTransport(type, header);
    }

    private static Type ToClrType(OpcUaDataType dataType)
        => dataType switch
        {
            OpcUaDataType.SByte => typeof(sbyte),
            OpcUaDataType.Byte => typeof(byte),
            OpcUaDataType.Int16 => typeof(short),
            OpcUaDataType.UInt16 => typeof(ushort),
            OpcUaDataType.Int32 => typeof(int),
            OpcUaDataType.UInt32 => typeof(uint),
            OpcUaDataType.Int64 => typeof(long),
            OpcUaDataType.UInt64 => typeof(ulong),
            OpcUaDataType.Float => typeof(float),
            OpcUaDataType.Double => typeof(double),
            _ => typeof(object)
        };
}

/// <summary>一帧已解码的 OPC UA 报文。内部字段按服务类型选用。</summary>
internal sealed class OpcUaMessage
{
    public OpcUaMessage(
        string type,
        uint channelId,
        uint? tokenId,
        uint sequenceNumber,
        uint requestId,
        uint typeId,
        string? text,
        OpcUaNodeId? authenticationToken,
        IReadOnlyList<OpcUaNodeId>? nodeIds,
        IReadOnlyList<OpcUaValue>? values)
    {
        Type = type;
        ChannelId = channelId;
        TokenId = tokenId;
        SequenceNumber = sequenceNumber;
        RequestId = requestId;
        TypeId = typeId;
        Text = text;
        AuthenticationToken = authenticationToken;
        NodeIds = nodeIds;
        Values = values;
    }

    public string Type { get; }

    public uint ChannelId { get; }

    public uint? TokenId { get; }

    public uint SequenceNumber { get; }

    public uint RequestId { get; }

    public uint TypeId { get; }

    public string? Text { get; }

    public string? Password { get; init; }

    public OpcUaNodeId? AuthenticationToken { get; }

    public OpcUaNodeId? SessionId { get; init; }

    public IReadOnlyList<OpcUaNodeId>? NodeIds { get; }

    public IReadOnlyList<OpcUaValue>? Values { get; }
}
