namespace Zeus;

/// <summary>声明 OPC UA NodeId 与 Zeus 点表之间的映射。</summary>
public sealed class OpcUaPointMap
{
    private readonly List<OpcUaPointSpec> _points = [];
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nodeIds = new(StringComparer.Ordinal);

    /// <summary>已声明的点，登记顺序。</summary>
    internal IReadOnlyList<OpcUaPointSpec> Points => _points;

    /// <summary>声明布尔点。</summary>
    public OpcUaPointMap Boolean(string name, string nodeId)
        => Add(name, nodeId, OpcUaDataType.Boolean, PointValueKind.Boolean, null, null);

    /// <summary>声明有符号 32 位整数点。</summary>
    public OpcUaPointMap Int32(string name, string nodeId, double? scale = null, PointAlarmLimits? alarmLimits = null)
        => Add(name, nodeId, OpcUaDataType.Int32, scale is null ? PointValueKind.Object : PointValueKind.Double, scale, alarmLimits);

    /// <summary>声明无符号 32 位整数点。</summary>
    public OpcUaPointMap UInt32(string name, string nodeId, double? scale = null, PointAlarmLimits? alarmLimits = null)
        => Add(name, nodeId, OpcUaDataType.UInt32, scale is null ? PointValueKind.Object : PointValueKind.Double, scale, alarmLimits);

    /// <summary>声明有符号 64 位整数点。</summary>
    public OpcUaPointMap Int64(string name, string nodeId, double? scale = null, PointAlarmLimits? alarmLimits = null)
        => Add(name, nodeId, OpcUaDataType.Int64, scale is null ? PointValueKind.Object : PointValueKind.Double, scale, alarmLimits);

    /// <summary>声明单精度浮点点。</summary>
    public OpcUaPointMap Float(string name, string nodeId, double? scale = null, PointAlarmLimits? alarmLimits = null)
        => Add(name, nodeId, OpcUaDataType.Float, PointValueKind.Double, scale, alarmLimits);

    /// <summary>声明双精度浮点点。</summary>
    public OpcUaPointMap Double(string name, string nodeId, double? scale = null, PointAlarmLimits? alarmLimits = null)
        => Add(name, nodeId, OpcUaDataType.Double, PointValueKind.Double, scale, alarmLimits);

    /// <summary>声明 UTF-8 文本点。</summary>
    public OpcUaPointMap String(string name, string nodeId)
        => Add(name, nodeId, OpcUaDataType.String, PointValueKind.Object, null, null);

    /// <summary>声明 UTC 时间点。</summary>
    public OpcUaPointMap DateTime(string name, string nodeId)
        => Add(name, nodeId, OpcUaDataType.DateTime, PointValueKind.Object, null, null);

    /// <summary>声明原始字节点。</summary>
    public OpcUaPointMap ByteString(string name, string nodeId)
        => Add(name, nodeId, OpcUaDataType.ByteString, PointValueKind.Object, null, null);

    /// <summary>按指定内置类型声明点。</summary>
    public OpcUaPointMap Typed(string name, string nodeId, OpcUaDataType dataType, double? scale = null, PointAlarmLimits? alarmLimits = null)
        => Add(name, nodeId, dataType, KindOf(dataType, scale), scale, alarmLimits);

    /// <summary>把已声明的点标为可写。</summary>
    public OpcUaPointMap Writable(string name)
    {
        var index = FindIndex(name);
        _points[index] = _points[index] with { Writable = true };
        return this;
    }

    /// <summary>为已声明的数值点设置报警限。</summary>
    public OpcUaPointMap WithAlarmLimits(string name, double? low = null, double? high = null)
    {
        if (low > high)
        {
            throw new ZeusException($"OPC UA 点 {name} 的低报警限不能高于高报警限。");
        }

        var index = FindIndex(name);
        var point = _points[index];
        if (!OpcUaCodec.IsNumeric(point.DataType))
        {
            throw new ZeusException($"OPC UA 点 {point.Name} 不是数值点，不能配置报警限。");
        }

        _points[index] = point with { AlarmLimits = new PointAlarmLimits(low, high) };
        return this;
    }

    private OpcUaPointMap Add(
        string name,
        string nodeId,
        OpcUaDataType dataType,
        PointValueKind kind,
        double? scale,
        PointAlarmLimits? alarmLimits)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ZeusException("OPC UA 点名不能为空。");
        }

        if (scale is <= 0)
        {
            throw new ZeusException($"OPC UA 点 {name} 的 scale 必须大于 0。");
        }

        if (alarmLimits?.Low > alarmLimits?.High)
        {
            throw new ZeusException($"OPC UA 点 {name} 的低报警限不能高于高报警限。");
        }

        if (alarmLimits is not null && !OpcUaCodec.IsNumeric(dataType))
        {
            throw new ZeusException($"OPC UA 点 {name} 不是数值点，不能配置报警限。");
        }

        var normalizedName = name.Trim();
        var parsed = OpcUaNodeId.Parse(nodeId);
        var key = parsed.ToString();
        if (!_names.Add(normalizedName))
        {
            throw new ZeusException($"同一台 OPC UA 设备上点名 {normalizedName} 重复。");
        }

        if (!_nodeIds.Add(key))
        {
            throw new ZeusException($"同一台 OPC UA 设备上 NodeId {key} 重复。");
        }

        _points.Add(new OpcUaPointSpec(normalizedName, parsed, dataType, kind, scale, alarmLimits, false));
        return this;
    }

    private int FindIndex(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ZeusException("OPC UA 点名不能为空。");
        }

        var normalized = name.Trim();
        for (var i = 0; i < _points.Count; i++)
        {
            if (string.Equals(_points[i].Name, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ZeusException($"找不到 OPC UA 点 {normalized}，请先声明该点。");
    }

    private static PointValueKind KindOf(OpcUaDataType dataType, double? scale)
        => dataType switch
        {
            OpcUaDataType.Boolean => PointValueKind.Boolean,
            OpcUaDataType.Float or OpcUaDataType.Double => PointValueKind.Double,
            _ when scale is not null && OpcUaCodec.IsNumeric(dataType) => PointValueKind.Double,
            _ => PointValueKind.Object
        };
}
