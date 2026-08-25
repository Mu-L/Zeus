using System.Globalization;

namespace Zeus;

/// <summary>
/// OPC UA NodeId。点表用 <c>ns=2;s=Temperature</c> 或 <c>i=2258</c> 这种规范文本声明。
/// </summary>
public sealed class OpcUaNodeId : IEquatable<OpcUaNodeId>
{
    /// <summary>创建 NodeId。</summary>
    /// <param name="namespaceIndex">命名空间索引，0 表示 OPC UA 标准命名空间。</param>
    /// <param name="identifierType">标识符形态。</param>
    /// <param name="identifier">标识符。数值为 <see cref="uint"/>，字符串为 <see cref="string"/>，GUID 为 <see cref="Guid"/>，不透明为 <see cref="byte"/>[]。</param>
    public OpcUaNodeId(ushort namespaceIndex, OpcUaNodeIdType identifierType, object identifier)
    {
        NamespaceIndex = namespaceIndex;
        IdentifierType = identifierType;
        Identifier = identifierType switch
        {
            OpcUaNodeIdType.Numeric => Convert.ToUInt32(identifier, CultureInfo.InvariantCulture),
            OpcUaNodeIdType.String => RequireText(identifier),
            OpcUaNodeIdType.Guid => identifier is Guid guid ? guid : Guid.Parse(Convert.ToString(identifier, CultureInfo.InvariantCulture) ?? string.Empty),
            OpcUaNodeIdType.Opaque => identifier is byte[] bytes ? bytes.ToArray() : throw new OpcUaException("不透明 NodeId 需要 byte[]。"),
            _ => throw new OpcUaException($"不支持的 NodeId 类型 {identifierType}。")
        };
    }

    /// <summary>命名空间索引。</summary>
    public ushort NamespaceIndex { get; }

    /// <summary>标识符形态。</summary>
    public OpcUaNodeIdType IdentifierType { get; }

    /// <summary>标识符值。</summary>
    public object Identifier { get; }

    /// <summary>创建数值 NodeId。</summary>
    public static OpcUaNodeId Numeric(uint identifier, ushort namespaceIndex = 0)
        => new(namespaceIndex, OpcUaNodeIdType.Numeric, identifier);

    /// <summary>创建字符串 NodeId。</summary>
    public static OpcUaNodeId String(string identifier, ushort namespaceIndex = 0)
        => new(namespaceIndex, OpcUaNodeIdType.String, identifier);

    /// <summary>
    /// 解析规范文本。支持 <c>i=2258</c>、<c>ns=2;s=Temperature</c>、<c>ns=1;g=...</c>、<c>ns=1;b=Base64</c>。
    /// </summary>
    public static OpcUaNodeId Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new OpcUaException("OPC UA NodeId 不能为空。");
        }

        try
        {
            var normalized = text.Trim();
            ushort namespaceIndex = 0;
            string? identifierPart = null;
            foreach (var segment in normalized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = segment.IndexOf('=');
                if (separator <= 0 || separator == segment.Length - 1)
                {
                    throw new OpcUaException($"OPC UA NodeId {text} 无效。");
                }

                var key = segment[..separator];
                var value = segment[(separator + 1)..];
                if (string.Equals(key, "ns", StringComparison.OrdinalIgnoreCase))
                {
                    namespaceIndex = ushort.Parse(value, CultureInfo.InvariantCulture);
                    continue;
                }

                identifierPart = segment;
            }

            if (identifierPart is null)
            {
                throw new OpcUaException($"OPC UA NodeId {text} 缺少标识符。");
            }

            var typeSeparator = identifierPart.IndexOf('=');
            var typeKey = identifierPart[..typeSeparator];
            var identifierText = identifierPart[(typeSeparator + 1)..];
            return typeKey.ToLowerInvariant() switch
            {
                "i" => Numeric(uint.Parse(identifierText, CultureInfo.InvariantCulture), namespaceIndex),
                "s" => String(identifierText, namespaceIndex),
                "g" => new OpcUaNodeId(namespaceIndex, OpcUaNodeIdType.Guid, Guid.Parse(identifierText)),
                "b" => new OpcUaNodeId(namespaceIndex, OpcUaNodeIdType.Opaque, Convert.FromBase64String(identifierText)),
                _ => throw new OpcUaException($"OPC UA NodeId {text} 的标识符类型不受支持。")
            };
        }
        catch (OpcUaException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new OpcUaException($"OPC UA NodeId {text} 无效：{ex.Message}");
        }
    }

    /// <summary>输出规范文本，便于 JSON 与日志对照。</summary>
    public override string ToString()
    {
        var identifier = IdentifierType switch
        {
            OpcUaNodeIdType.Numeric => "i=" + Convert.ToString(Identifier, CultureInfo.InvariantCulture),
            OpcUaNodeIdType.String => "s=" + Identifier,
            OpcUaNodeIdType.Guid => "g=" + ((Guid)Identifier).ToString("D"),
            OpcUaNodeIdType.Opaque => "b=" + Convert.ToBase64String((byte[])Identifier),
            _ => throw new OpcUaException($"不支持的 NodeId 类型 {IdentifierType}。")
        };
        return NamespaceIndex == 0 ? identifier : "ns=" + NamespaceIndex.ToString(CultureInfo.InvariantCulture) + ";" + identifier;
    }

    /// <inheritdoc />
    public bool Equals(OpcUaNodeId? other)
    {
        if (other is null)
        {
            return false;
        }

        if (NamespaceIndex != other.NamespaceIndex || IdentifierType != other.IdentifierType)
        {
            return false;
        }

        return IdentifierType == OpcUaNodeIdType.Opaque
            ? ((byte[])Identifier).AsSpan().SequenceEqual((byte[])other.Identifier)
            : Equals(Identifier, other.Identifier);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as OpcUaNodeId);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        if (IdentifierType == OpcUaNodeIdType.Opaque)
        {
            var bytes = (byte[])Identifier;
            var hash = new HashCode();
            hash.Add(NamespaceIndex);
            hash.Add(IdentifierType);
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }

        return HashCode.Combine(NamespaceIndex, IdentifierType, Identifier);
    }

    private static string RequireText(object identifier)
    {
        var text = Convert.ToString(identifier, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new OpcUaException("字符串 NodeId 不能为空。");
        }

        return text;
    }
}
