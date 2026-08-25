namespace Zeus;

/// <summary>OPC UA 虚拟 Server 的内存地址空间。只保存 Value 属性。</summary>
public sealed class OpcUaServerMemory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OpcUaValue> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _writable = new(StringComparer.Ordinal);

    /// <summary>创建内存地址空间，并预置 ServerStatus.CurrentTime。</summary>
    public OpcUaServerMemory()
    {
        Set("i=2258", OpcUaValue.DateTime(DateTime.UtcNow), writable: false);
        SetText("ns=2;s=ServerName", "zeus", writable: true);
    }

    /// <summary>设置节点值。</summary>
    public void Set(string nodeId, OpcUaValue value, bool writable = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        var key = OpcUaNodeId.Parse(nodeId).ToString();
        lock (_gate)
        {
            _values[key] = value;
            if (writable)
            {
                _writable.Add(key);
            }
            else
            {
                _writable.Remove(key);
            }
        }
    }

    /// <summary>设置双精度节点。</summary>
    public void SetDouble(string nodeId, double value, bool writable = true) => Set(nodeId, OpcUaValue.Double(value), writable);

    /// <summary>设置 32 位整数节点。</summary>
    public void SetInt32(string nodeId, int value, bool writable = true) => Set(nodeId, OpcUaValue.Int32(value), writable);

    /// <summary>设置布尔节点。</summary>
    public void SetBoolean(string nodeId, bool value, bool writable = true) => Set(nodeId, OpcUaValue.Boolean(value), writable);

    /// <summary>设置文本节点。</summary>
    public void SetText(string nodeId, string value, bool writable = true) => Set(nodeId, OpcUaValue.String(value), writable);

    /// <summary>读取节点值。</summary>
    public bool TryGet(string nodeId, out OpcUaValue? value)
    {
        var key = OpcUaNodeId.Parse(nodeId).ToString();
        lock (_gate)
        {
            return _values.TryGetValue(key, out value);
        }
    }

    /// <summary>尝试写入节点。未知 NodeId 或只读节点返回对应 StatusCode。</summary>
    public uint TrySet(string nodeId, OpcUaValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var key = OpcUaNodeId.Parse(nodeId).ToString();
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out var existing))
            {
                return OpcUaStatusCodes.BadNodeIdUnknown;
            }

            if (!_writable.Contains(key))
            {
                return OpcUaStatusCodes.BadNotWritable;
            }

            try
            {
                _values[key] = OpcUaCodec.Coerce(value, existing.DataType);
                return OpcUaStatusCodes.Good;
            }
            catch (Exception)
            {
                return OpcUaStatusCodes.BadTypeMismatch;
            }
        }
    }
}
