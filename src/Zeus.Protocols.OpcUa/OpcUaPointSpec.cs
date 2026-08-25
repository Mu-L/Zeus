namespace Zeus;

/// <summary>一个 OPC UA NodeId 点的声明。</summary>
public sealed record OpcUaPointSpec(
    string Name,
    OpcUaNodeId NodeId,
    OpcUaDataType DataType,
    PointValueKind Kind,
    double? Scale,
    PointAlarmLimits? AlarmLimits,
    bool Writable);
