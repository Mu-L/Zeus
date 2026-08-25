using Microsoft.Extensions.Logging;

namespace Zeus;

/// <summary>把 OPC UA NodeId 映射为 Zeus 点表的设备。</summary>
public sealed class OpcUaDevice : DeviceBase, IAcquisitionSource, IPointWriter, IAsyncDisposable
{
    private readonly OpcUaClient _client;
    private readonly IReadOnlyList<OpcUaPointSpec> _specs;
    private readonly IReadOnlyList<PointDefinition> _points;

    /// <summary>创建 OPC UA 设备。</summary>
    public OpcUaDevice(
        string name,
        IChannel channel,
        OpcUaOptions? options = null,
        TimeSpan? timeout = null,
        OpcUaPointMap? pointMap = null,
        ILogger<OpcUaDevice>? logger = null)
        : base(name, channel, logger)
    {
        _client = new OpcUaClient(channel, options, timeout, name);
        _specs = pointMap?.Points.ToArray() ?? [];
        _points = _specs.Select(spec => new PointDefinition(spec.Name, Name, spec.Kind, spec.AlarmLimits, spec.Writable)).ToArray();
    }

    /// <summary>底层 OPC UA 客户端。</summary>
    public OpcUaClient Client => _client;

    /// <summary>设备点表定义。</summary>
    public IReadOnlyList<PointDefinition> Points => _points;

    /// <summary>读取一个 NodeId。</summary>
    public Task<OpcUaValue> ReadNodeAsync(string nodeId, CancellationToken cancellationToken = default)
        => _client.ReadAsync(nodeId, cancellationToken);

    /// <summary>写入一个 NodeId。</summary>
    public Task WriteNodeAsync(string nodeId, OpcUaValue value, CancellationToken cancellationToken = default)
        => _client.WriteAsync(nodeId, value, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PointReadResult>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_specs.Count == 0)
        {
            return [];
        }

        var results = new List<PointReadResult>(_specs.Count);
        try
        {
            var values = await _client.ReadAsync(_specs.Select(spec => spec.NodeId).ToArray(), cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < _specs.Count; i++)
            {
                var spec = _specs[i];
                var qualified = Name + "." + spec.Name;
                try
                {
                    var coerced = OpcUaCodec.Coerce(values[i], spec.DataType);
                    results.Add(PointReadResult.Success(qualified, OpcUaCodec.ToEngineeringValue(coerced, spec.Scale)));
                }
                catch (Exception ex)
                {
                    LogAcquisitionFailed(ex, spec.Name);
                    results.Add(PointReadResult.Failure(qualified, ex.Message));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogAcquisitionFailed(ex);
            foreach (var spec in _specs)
            {
                results.Add(PointReadResult.Failure(Name + "." + spec.Name, ex.Message));
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task WriteAsync(string pointName, object value, IPointTableWriter table, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(table);
        var spec = FindSpec(pointName);
        var qualified = Name + "." + spec.Name;
        try
        {
            if (!spec.Writable)
            {
                throw new ZeusException($"点 {qualified} 未标为可写。");
            }

            var wireValue = OpcUaCodec.FromEngineeringValue(spec.DataType, value, spec.Scale);
            await _client.WriteAsync(spec.NodeId.ToString(), wireValue, cancellationToken).ConfigureAwait(false);
            table.Publish(qualified, OpcUaCodec.ToEngineeringValue(wireValue, spec.Scale));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogWriteFailed(ex, spec.Name);
            table.PublishError(qualified, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private OpcUaPointSpec FindSpec(string pointName)
    {
        if (string.IsNullOrWhiteSpace(pointName))
        {
            throw new ZeusException("写回点名不能为空。");
        }

        var key = pointName.Trim();
        return _specs.FirstOrDefault(spec => string.Equals(spec.Name, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new ZeusException($"设备 {Name} 上找不到点 {key}。");
    }
}
