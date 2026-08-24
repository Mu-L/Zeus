using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Zeus;

/// <summary>
/// 线程安全的内存点表。采集循环写入快照；业务与界面按名称读取，也可按名称写回设备。
/// </summary>
public sealed class PointTable : IPointTable, IPointTableWriter
{
    private readonly object _gate = new();
    private readonly IDeviceRegistry? _devices;
    private readonly ILogger _logger;
    private readonly List<string> _order = [];
    private readonly Dictionary<string, PointSnapshot> _byQualified = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _shortToQualified = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ambiguousShortNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Pattern, EventHandler<PointChangedEventArgs> Handler)> _subscriptions = [];
    private readonly List<PointChangedEventArgs> _batch = [];
    private int _batchDepth;

    /// <summary>
    /// 创建未连接设备目录的点表。可以读写快照，但不能 <see cref="WriteAsync"/>。
    /// </summary>
    public PointTable()
        : this(null, null)
    {
    }

    /// <summary>
    /// 创建连接到设备目录的点表。宿主通过本构造函数注入目录，以便按点名路由写回。
    /// </summary>
    /// <param name="devices">设备目录。为 <c>null</c> 时禁止写回。</param>
    public PointTable(IDeviceRegistry? devices)
        : this(devices, null)
    {
    }

    /// <summary>
    /// 创建连接到设备目录的点表，并配置诊断日志。
    /// </summary>
    /// <param name="devices">设备目录。为 <c>null</c> 时禁止写回。</param>
    /// <param name="logger">写回失败时的诊断日志。允许为 <c>null</c>。</param>
    public PointTable(IDeviceRegistry? devices, ILogger? logger)
    {
        _devices = devices;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        if (devices is not null)
        {
            // 设备一登记就把点挂进点表，避免界面在 StartAsync 前扫到空表。
            devices.Changed += OnDevicesChanged;
            foreach (var device in devices.All)
            {
                RegisterDevicePoints(device);
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<PointChangedEventArgs>? Changed;

    /// <inheritdoc />
    public event EventHandler<PointBatchChangedEventArgs>? BatchChanged;

    /// <inheritdoc />
    public IReadOnlyList<PointSnapshot> All
    {
        get
        {
            lock (_gate)
            {
                return _order.Select(name => _byQualified[name]).ToArray();
            }
        }
    }

    /// <inheritdoc />
    public PointSnapshot Get(string name)
    {
        if (!TryGetSnapshot(name, out var snapshot) || snapshot is null)
        {
            throw CreateMissingException(name);
        }

        return snapshot;
    }

    /// <inheritdoc />
    public bool TryGet(string name, out PointSnapshot? snapshot)
        => TryGetSnapshot(name, out snapshot);

    /// <inheritdoc />
    public bool TryGetDouble(string name, out double value)
    {
        value = 0;
        return TryGetSnapshot(name, out var snapshot)
            && snapshot is not null
            && snapshot.TryGetDouble(out value);
    }

    /// <inheritdoc />
    public IDisposable Subscribe(string name, EventHandler<PointChangedEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ZeusException("订阅点名不能为空。");
        }

        var pattern = name.Trim();
        lock (_gate)
        {
            _subscriptions.Add((pattern, handler));
        }

        return new DelegateSubscription(() =>
        {
            lock (_gate)
            {
                for (var i = _subscriptions.Count - 1; i >= 0; i--)
                {
                    if (_subscriptions[i].Pattern == pattern && _subscriptions[i].Handler == handler)
                    {
                        _subscriptions.RemoveAt(i);
                        break;
                    }
                }
            }
        });
    }

    /// <inheritdoc />
    public T Get<T>(string name)
    {
        var snapshot = Get(name);
        if (snapshot.Value is null)
        {
            throw new ZeusException(
                $"点 {snapshot.QualifiedName} 尚无有效值。请等待采集循环完成第一轮，或检查 Error：{snapshot.Error ?? "无"}。");
        }

        if (TryConvert(snapshot.Value, out T? typed) && typed is not null)
        {
            return typed;
        }

        throw new ZeusException(
            $"点 {snapshot.QualifiedName} 的实际类型为 {snapshot.Value.GetType().Name}，无法作为 {typeof(T).Name} 读取。");
    }

    /// <inheritdoc />
    public bool TryGet<T>(string name, out T? value)
    {
        value = default;
        if (!TryGetSnapshot(name, out var snapshot) || snapshot?.Value is null)
        {
            return false;
        }

        return TryConvert(snapshot.Value, out value);
    }

    /// <inheritdoc />
    public void Register(PointDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            if (_byQualified.ContainsKey(definition.QualifiedName))
            {
                // 采集循环与设备目录都可能登记同一批点；重复登记视为幂等。
                return;
            }

            _byQualified[definition.QualifiedName] = new PointSnapshot(definition, null, null, null);
            _order.Add(definition.QualifiedName);

            if (_ambiguousShortNames.Contains(definition.Name))
            {
                return;
            }

            if (_shortToQualified.ContainsKey(definition.Name))
            {
                _shortToQualified.Remove(definition.Name);
                _ambiguousShortNames.Add(definition.Name);
            }
            else
            {
                _shortToQualified[definition.Name] = definition.QualifiedName;
            }
        }
    }

    /// <inheritdoc />
    public void Publish(string qualifiedName, object? value)
    {
        PointSnapshot? previous;
        PointSnapshot current;
        lock (_gate)
        {
            if (!_byQualified.TryGetValue(qualifiedName, out var existing))
            {
                throw new ZeusException($"无法写入未登记的点 {qualifiedName}。请先在设备上声明点表。");
            }

            if (existing.Error is null && Equals(existing.Value, value))
            {
                return;
            }

            previous = existing;
            current = new PointSnapshot(existing.Definition, value, DateTimeOffset.Now, null, existing.AlarmState);
            _byQualified[qualifiedName] = current;
        }

        RaiseChanged(previous, current);
    }

    /// <inheritdoc />
    public void PublishError(string qualifiedName, string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ZeusException("采集错误说明不能为空。");
        }

        PointSnapshot? previous;
        PointSnapshot current;
        lock (_gate)
        {
            if (!_byQualified.TryGetValue(qualifiedName, out var existing))
            {
                throw new ZeusException($"无法写入未登记的点 {qualifiedName}。请先在设备上声明点表。");
            }

            if (string.Equals(existing.Error, error, StringComparison.Ordinal))
            {
                return;
            }

            previous = existing;
            current = new PointSnapshot(existing.Definition, existing.Value, existing.UpdatedAt, error, existing.AlarmState);
            _byQualified[qualifiedName] = current;
        }

        RaiseChanged(previous, current);
    }

    /// <inheritdoc />
    public void BeginBatch()
    {
        lock (_gate)
        {
            _batchDepth++;
        }
    }

    /// <inheritdoc />
    public void EndBatch()
    {
        PointChangedEventArgs[] changes;
        lock (_gate)
        {
            if (_batchDepth == 0)
            {
                return;
            }

            _batchDepth--;
            if (_batchDepth > 0)
            {
                return;
            }

            changes = _batch.ToArray();
            _batch.Clear();
        }

        BatchChanged?.Invoke(this, new PointBatchChangedEventArgs(changes));
    }

    /// <inheritdoc />
    public void Unregister(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            return;
        }

        lock (_gate)
        {
            RemoveLocked(qualifiedName.Trim());
        }
    }

    /// <inheritdoc />
    public void UnregisterDevice(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }

        var key = deviceName.Trim();
        lock (_gate)
        {
            var doomed = _order
                .Where(name => _byQualified.TryGetValue(name, out var snapshot)
                    && string.Equals(snapshot.Definition.DeviceName, key, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var qualified in doomed)
            {
                RemoveLocked(qualified);
            }
        }
    }

    /// <summary>
    /// 在已持有锁的前提下摘除一个点，并重建短名索引。
    /// </summary>
    private void RemoveLocked(string qualifiedName)
    {
        if (!_byQualified.Remove(qualifiedName, out var snapshot))
        {
            return;
        }

        _order.Remove(qualifiedName);
        RebuildShortNameIndex(snapshot.Definition.Name);
    }

    /// <summary>
    /// 某个短名对应的点被移除后，按剩余点重建短名到限定名的映射。
    /// </summary>
    private void RebuildShortNameIndex(string shortName)
    {
        _shortToQualified.Remove(shortName);
        _ambiguousShortNames.Remove(shortName);

        string? unique = null;
        foreach (var name in _order)
        {
            if (!_byQualified.TryGetValue(name, out var snapshot)
                || !string.Equals(snapshot.Definition.Name, shortName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (unique is not null)
            {
                unique = null;
                _ambiguousShortNames.Add(shortName);
                return;
            }

            unique = name;
        }

        if (unique is not null)
        {
            _shortToQualified[shortName] = unique;
        }
    }

    private bool TryGetSnapshot(string name, out PointSnapshot? snapshot)
    {
        snapshot = null;
        if (!TryResolveQualifiedName(name, out var qualifiedName) || qualifiedName is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _byQualified.TryGetValue(qualifiedName, out snapshot);
        }
    }

    private bool TryResolveQualifiedName(string name, out string? qualifiedName)
    {
        qualifiedName = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var key = name.Trim();
        lock (_gate)
        {
            if (_byQualified.ContainsKey(key))
            {
                qualifiedName = key;
                return true;
            }

            if (_ambiguousShortNames.Contains(key))
            {
                throw new ZeusException(
                    $"点名 {key} 在多台设备上重复。请使用限定名，例如 oven.{key}。");
            }

            if (_shortToQualified.TryGetValue(key, out var qualified)
                && _byQualified.ContainsKey(qualified))
            {
                qualifiedName = qualified;
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task WriteAsync(string name, object value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!TryResolveQualifiedName(name, out var qualifiedName) || qualifiedName is null)
        {
            throw CreateMissingException(name);
        }

        PointSnapshot snapshot;
        lock (_gate)
        {
            snapshot = _byQualified[qualifiedName];
        }

        var definition = snapshot.Definition;
        if (!definition.Writable)
        {
            throw new ZeusException(
                $"点 {definition.QualifiedName} 是只读点，不能写回。请在声明时把该点标为可写，例如 HoldingRegister(\"{definition.Name}\", address, writable: true)。");
        }

        if (_devices is null)
        {
            throw new ZeusException(
                $"点表未连接到设备目录，无法写回 {definition.QualifiedName}。请通过 ZeusHost 使用点表，而不是单独 new PointTable。");
        }

        if (!_devices.TryGet<IDevice>(definition.DeviceName, out var device) || device is null)
        {
            throw new ZeusException($"点 {definition.QualifiedName} 所属设备 {definition.DeviceName} 已不存在。");
        }

        if (device is not IPointWriter writer)
        {
            throw new ZeusException(
                $"设备 {device.Name}（{device.GetType().Name}）未实现 IPointWriter，不能按点名写回。自定义设备请实现该接口。");
        }

        try
        {
            await writer.WriteAsync(definition.Name, value, this, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ZeusException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 设备未按约定写入错误快照时，点表仍补上 Error，避免界面只看到异常、看不到点状态。
            using (LogScope.Begin(_logger, new Dictionary<string, object>
            {
                ["Device"] = definition.DeviceName,
                ["Point"] = definition.Name
            }))
            {
                _logger.LogWarning(
                    ZeusLogEvents.PointWriteFailed,
                    ex,
                    "点 {Point} 写回失败。",
                    definition.QualifiedName);
            }

            PublishError(definition.QualifiedName, ex.Message);
            throw;
        }
    }

    private void OnDevicesChanged(object? sender, DeviceRegistryChangedEventArgs e)
    {
        if (e.Change == DeviceRegistryChange.Added)
        {
            RegisterDevicePoints(e.Device);
            return;
        }

        UnregisterDevice(e.Device.Name);
    }

    /// <summary>
    /// 把采集源声明的点立刻挂进点表，这样宿主构建完成后即可按名查找。
    /// </summary>
    private void RegisterDevicePoints(IDevice device)
    {
        if (device is not IAcquisitionSource source)
        {
            return;
        }

        foreach (var point in source.Points)
        {
            Register(point);
        }
    }

    /// <summary>
    /// 触发逐点变化，并按当前批次深度决定是否立即发布 <see cref="BatchChanged"/>。
    /// </summary>
    private void RaiseChanged(PointSnapshot? previous, PointSnapshot current)
    {
        var args = new PointChangedEventArgs(previous, current);
        EventHandler<PointChangedEventArgs>[] targeted;
        var emitImmediateBatch = false;
        lock (_gate)
        {
            targeted = _subscriptions
                .Where(item => MatchesSubscription(item.Pattern, current))
                .Select(item => item.Handler)
                .ToArray();
            if (_batchDepth > 0)
            {
                _batch.Add(args);
            }
            else
            {
                emitImmediateBatch = true;
            }
        }

        Changed?.Invoke(this, args);
        foreach (var handler in targeted)
        {
            handler(this, args);
        }

        if (emitImmediateBatch)
        {
            BatchChanged?.Invoke(this, new PointBatchChangedEventArgs([args]));
        }
    }

    private static bool MatchesSubscription(string pattern, PointSnapshot snapshot)
        => snapshot.QualifiedName.Equals(pattern, StringComparison.OrdinalIgnoreCase)
            || snapshot.Definition.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    private ZeusException CreateMissingException(string name)
    {
        lock (_gate)
        {
            var available = _order.Count == 0
                ? "当前尚未登记任何点"
                : "已登记：" + string.Join("、", _order);
            return new ZeusException($"找不到名为 {name} 的点。{available}。");
        }
    }

    private static bool TryConvert<T>(object value, out T? typed)
    {
        if (value is T direct)
        {
            typed = direct;
            return true;
        }

        try
        {
            typed = (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            typed = default;
            return false;
        }
    }

    /// <summary>
    /// 点订阅句柄。释放时从点表退订。
    /// </summary>
    private sealed class DelegateSubscription : IDisposable
    {
        private Action? _dispose;

        public DelegateSubscription(Action dispose) => _dispose = dispose;

        public void Dispose()
        {
            var action = Interlocked.Exchange(ref _dispose, null);
            action?.Invoke();
        }
    }
}
