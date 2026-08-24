namespace Zeus;

/// <summary>
/// 内存报警队列。同一点同时只保留一条未复归记录；复归后进入有上限的历史。
/// 支持严重等级、责任人、搁置与点抑制。
/// </summary>
public sealed class PointAlarmTable : IPointAlarmTable
{
    private const int DefaultHistoryCapacity = 256;
    private readonly object _gate = new();
    private readonly int _historyCapacity;
    private readonly IPointTable _points;
    private readonly Dictionary<string, PointAlarmRecord> _activeByPoint = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> _idToPoint = [];
    private readonly List<PointAlarmRecord> _history = [];
    private readonly HashSet<string> _suppressed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 创建报警队列并订阅点表变化。
    /// </summary>
    /// <param name="points">宿主点表。</param>
    /// <param name="historyCapacity">已复归记录保留条数。</param>
    public PointAlarmTable(IPointTable points, int historyCapacity = DefaultHistoryCapacity)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (historyCapacity < 0)
        {
            throw new ZeusException("报警历史容量不能为负数。");
        }

        _points = points;
        _historyCapacity = historyCapacity;
        points.Changed += OnPointChanged;
    }

    /// <inheritdoc />
    public event EventHandler<PointAlarmChangedEventArgs>? Changed;

    /// <inheritdoc />
    public IReadOnlyList<PointAlarmRecord> Active
    {
        get
        {
            lock (_gate)
            {
                ExpireShelvesLocked(DateTimeOffset.Now);
                return _activeByPoint.Values
                    .Where(item => item.IsOpen)
                    .OrderByDescending(item => item.Severity)
                    .ThenBy(item => item.RaisedAt)
                    .ToArray();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PointAlarmRecord> History
    {
        get
        {
            lock (_gate)
            {
                return _history.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public PointAlarmRecord Acknowledge(Guid id, string? acknowledgedBy = null)
    {
        PointAlarmRecord? previous;
        PointAlarmRecord current;
        lock (_gate)
        {
            var existing = RequireOpen(id);
            previous = existing;
            current = AcknowledgeLocked(existing, acknowledgedBy);
        }

        if (!ReferenceEquals(previous, current))
        {
            Changed?.Invoke(this, new PointAlarmChangedEventArgs(previous, current));
        }

        return current;
    }

    /// <inheritdoc />
    public PointAlarmRecord? AcknowledgePoint(string pointName, string? acknowledgedBy = null)
    {
        if (string.IsNullOrWhiteSpace(pointName))
        {
            throw new ZeusException("确认报警时点名不能为空。");
        }

        PointAlarmRecord? previous;
        PointAlarmRecord current;
        lock (_gate)
        {
            var existing = FindByNameLocked(pointName.Trim());
            if (existing is null || !existing.IsOpen)
            {
                return null;
            }

            previous = existing;
            current = AcknowledgeLocked(existing, acknowledgedBy);
        }

        if (!ReferenceEquals(previous, current))
        {
            Changed?.Invoke(this, new PointAlarmChangedEventArgs(previous, current));
        }

        return current;
    }

    /// <inheritdoc />
    public IReadOnlyList<PointAlarmRecord> AcknowledgeAll(string? acknowledgedBy = null)
    {
        var changes = new List<(PointAlarmRecord Previous, PointAlarmRecord Current)>();
        lock (_gate)
        {
            foreach (var existing in _activeByPoint.Values.Where(item => item.IsOpen).ToArray())
            {
                var updated = AcknowledgeLocked(existing, acknowledgedBy);
                if (!ReferenceEquals(existing, updated))
                {
                    changes.Add((existing, updated));
                }
            }
        }

        foreach (var change in changes)
        {
            Changed?.Invoke(this, new PointAlarmChangedEventArgs(change.Previous, change.Current));
        }

        return changes.Select(item => item.Current).ToArray();
    }

    /// <inheritdoc />
    public PointAlarmRecord Assign(Guid id, string? assignee)
    {
        PointAlarmRecord previous;
        PointAlarmRecord current;
        lock (_gate)
        {
            previous = RequireStored(id);
            current = Clone(
                previous,
                previous.Status,
                previous.Value,
                previous.AcknowledgedAt,
                previous.ClearedAt,
                previous.AcknowledgedBy,
                string.IsNullOrWhiteSpace(assignee) ? null : assignee.Trim(),
                previous.ShelvedUntil,
                previous.Suppressed);
            _activeByPoint[previous.QualifiedName] = current;
        }

        Changed?.Invoke(this, new PointAlarmChangedEventArgs(previous, current));
        return current;
    }

    /// <inheritdoc />
    public PointAlarmRecord Shelve(Guid id, DateTimeOffset until)
    {
        if (until <= DateTimeOffset.Now)
        {
            throw new ZeusException("搁置截止时间必须晚于当前时间。");
        }

        PointAlarmRecord previous;
        PointAlarmRecord current;
        lock (_gate)
        {
            previous = RequireOpen(id);
            current = Clone(
                previous,
                PointAlarmStatus.Shelved,
                previous.Value,
                previous.AcknowledgedAt,
                previous.ClearedAt,
                previous.AcknowledgedBy,
                previous.Assignee,
                until,
                previous.Suppressed);
            _activeByPoint[previous.QualifiedName] = current;
        }

        Changed?.Invoke(this, new PointAlarmChangedEventArgs(previous, current));
        return current;
    }

    /// <inheritdoc />
    public PointAlarmRecord Unshelve(Guid id)
    {
        PointAlarmRecord previous;
        PointAlarmRecord current;
        lock (_gate)
        {
            previous = RequireStored(id);
            if (previous.Status != PointAlarmStatus.Shelved)
            {
                return previous;
            }

            current = Clone(
                previous,
                previous.AcknowledgedAt is null ? PointAlarmStatus.Active : PointAlarmStatus.Acknowledged,
                previous.Value,
                previous.AcknowledgedAt,
                previous.ClearedAt,
                previous.AcknowledgedBy,
                previous.Assignee,
                null,
                previous.Suppressed);
            _activeByPoint[previous.QualifiedName] = current;
        }

        Changed?.Invoke(this, new PointAlarmChangedEventArgs(previous, current));
        return current;
    }

    /// <inheritdoc />
    public void Suppress(string pointName)
    {
        var key = ResolvePointKey(pointName);
        PointAlarmRecord? previous = null;
        PointAlarmRecord? current = null;
        lock (_gate)
        {
            _suppressed.Add(key);
            if (_activeByPoint.TryGetValue(key, out var existing) ||
                (existing = FindByNameLocked(key)) is not null)
            {
                previous = existing;
                current = Clone(
                    existing,
                    PointAlarmStatus.Shelved,
                    existing.Value,
                    existing.AcknowledgedAt,
                    existing.ClearedAt,
                    existing.AcknowledgedBy,
                    existing.Assignee,
                    existing.ShelvedUntil,
                    suppressed: true);
                _activeByPoint[existing.QualifiedName] = current;
            }
        }

        if (previous is not null && current is not null)
        {
            Changed?.Invoke(this, new PointAlarmChangedEventArgs(previous, current));
        }
    }

    /// <inheritdoc />
    public void Unsuppress(string pointName)
    {
        var key = ResolvePointKey(pointName);
        lock (_gate)
        {
            _suppressed.Remove(key);
            if (_activeByPoint.TryGetValue(key, out var existing) ||
                (existing = FindByNameLocked(key)) is not null)
            {
                var restored = Clone(
                    existing,
                    existing.AcknowledgedAt is null ? PointAlarmStatus.Active : PointAlarmStatus.Acknowledged,
                    existing.Value,
                    existing.AcknowledgedAt,
                    existing.ClearedAt,
                    existing.AcknowledgedBy,
                    existing.Assignee,
                    null,
                    suppressed: false);
                _activeByPoint[existing.QualifiedName] = restored;
            }
        }
    }

    /// <inheritdoc />
    public bool IsSuppressed(string pointName)
    {
        var key = pointName?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        lock (_gate)
        {
            return _suppressed.Contains(key)
                || _suppressed.Contains(ResolvePointKey(key));
        }
    }

    /// <summary>
    /// 设备卸载时把该设备未复归报警标为已复归，避免队列留下悬空点名。
    /// </summary>
    /// <param name="deviceName">设备名。</param>
    public void ClearDevice(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }

        var key = deviceName.Trim();
        var changes = new List<(PointAlarmRecord Previous, PointAlarmRecord Current)>();
        lock (_gate)
        {
            foreach (var existing in _activeByPoint.Values
                .Where(item => item.DeviceName.Equals(key, StringComparison.OrdinalIgnoreCase))
                .ToArray())
            {
                changes.Add((existing, ClearLocked(existing, existing.Value, DateTimeOffset.Now)));
            }

            foreach (var name in _suppressed.Where(item => item.StartsWith(key + ".", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _suppressed.Remove(name);
            }
        }

        foreach (var change in changes)
        {
            Changed?.Invoke(this, new PointAlarmChangedEventArgs(change.Previous, change.Current));
        }
    }

    private void OnPointChanged(object? sender, PointChangedEventArgs e)
    {
        var snapshot = e.Current;
        if (snapshot.IsAlarmed)
        {
            RaiseOrRefresh(snapshot);
            return;
        }

        if (snapshot.AlarmState is PointAlarmState.Normal or PointAlarmState.Disabled)
        {
            ClearIfOpen(snapshot);
        }
    }

    private void RaiseOrRefresh(PointSnapshot snapshot)
    {
        PointAlarmRecord? previous;
        PointAlarmRecord current;
        lock (_gate)
        {
            ExpireShelvesLocked(DateTimeOffset.Now);
            var limits = snapshot.Definition.AlarmLimits;
            var suppressed = _suppressed.Contains(snapshot.QualifiedName) || _suppressed.Contains(snapshot.Definition.Name);
            if (_activeByPoint.TryGetValue(snapshot.QualifiedName, out var existing))
            {
                previous = existing;
                current = Clone(
                    existing,
                    existing.Status,
                    snapshot.Value,
                    existing.AcknowledgedAt,
                    existing.ClearedAt,
                    existing.AcknowledgedBy,
                    existing.Assignee,
                    existing.ShelvedUntil,
                    suppressed,
                    snapshot.AlarmState,
                    limits?.Severity ?? existing.Severity,
                    limits?.Area ?? existing.Area);
                _activeByPoint[snapshot.QualifiedName] = current;
            }
            else
            {
                if (suppressed)
                {
                    return;
                }

                previous = null;
                current = new PointAlarmRecord(
                    Guid.NewGuid(),
                    snapshot.QualifiedName,
                    snapshot.Definition.Name,
                    snapshot.Definition.DeviceName,
                    snapshot.AlarmState,
                    PointAlarmStatus.Active,
                    snapshot.Value,
                    snapshot.UpdatedAt ?? DateTimeOffset.Now,
                    null,
                    null,
                    null,
                    limits?.Severity ?? PointAlarmSeverity.Warning,
                    limits?.Area,
                    limits?.DefaultAssignee);
                _activeByPoint[snapshot.QualifiedName] = current;
                _idToPoint[current.Id] = snapshot.QualifiedName;
            }
        }

        if (previous is null
            || previous.AlarmState != current.AlarmState
            || previous.Status != current.Status
            || previous.Severity != current.Severity
            || previous.Assignee != current.Assignee
            || !Equals(previous.Value, current.Value))
        {
            Changed?.Invoke(this, new PointAlarmChangedEventArgs(previous, current));
        }
    }

    private void ClearIfOpen(PointSnapshot snapshot)
    {
        PointAlarmRecord? previous;
        PointAlarmRecord current;
        lock (_gate)
        {
            if (!_activeByPoint.TryGetValue(snapshot.QualifiedName, out var existing))
            {
                return;
            }

            previous = existing;
            current = ClearLocked(existing, snapshot.Value, snapshot.UpdatedAt ?? DateTimeOffset.Now);
        }

        Changed?.Invoke(this, new PointAlarmChangedEventArgs(previous, current));
    }

    private PointAlarmRecord AcknowledgeLocked(PointAlarmRecord existing, string? acknowledgedBy)
    {
        if (existing.Status != PointAlarmStatus.Active)
        {
            return existing;
        }

        var current = Clone(
            existing,
            PointAlarmStatus.Acknowledged,
            existing.Value,
            DateTimeOffset.Now,
            existing.ClearedAt,
            string.IsNullOrWhiteSpace(acknowledgedBy) ? null : acknowledgedBy.Trim(),
            existing.Assignee,
            existing.ShelvedUntil,
            existing.Suppressed);
        _activeByPoint[existing.QualifiedName] = current;
        return current;
    }

    private PointAlarmRecord ClearLocked(PointAlarmRecord existing, object? value, DateTimeOffset clearedAt)
    {
        var current = Clone(
            existing,
            PointAlarmStatus.Cleared,
            value,
            existing.AcknowledgedAt,
            clearedAt,
            existing.AcknowledgedBy,
            existing.Assignee,
            null,
            existing.Suppressed);
        _activeByPoint.Remove(existing.QualifiedName);
        _idToPoint.Remove(existing.Id);
        _history.Add(current);
        if (_history.Count > _historyCapacity)
        {
            _history.RemoveRange(0, _history.Count - _historyCapacity);
        }

        return current;
    }

    private void ExpireShelvesLocked(DateTimeOffset now)
    {
        foreach (var existing in _activeByPoint.Values.Where(item =>
                     item.Status == PointAlarmStatus.Shelved
                     && item.ShelvedUntil is { } until
                     && until <= now
                     && !item.Suppressed).ToArray())
        {
            var restored = Clone(
                existing,
                existing.AcknowledgedAt is null ? PointAlarmStatus.Active : PointAlarmStatus.Acknowledged,
                existing.Value,
                existing.AcknowledgedAt,
                existing.ClearedAt,
                existing.AcknowledgedBy,
                existing.Assignee,
                null,
                existing.Suppressed);
            _activeByPoint[existing.QualifiedName] = restored;
        }
    }

    private PointAlarmRecord RequireOpen(Guid id)
    {
        var existing = RequireStored(id);
        if (!existing.IsOpen && existing.Status != PointAlarmStatus.Shelved)
        {
            throw new ZeusException($"找不到标识为 {id} 的活动报警。请确认该报警尚未复归。");
        }

        return existing;
    }

    private PointAlarmRecord RequireStored(Guid id)
    {
        if (!_idToPoint.TryGetValue(id, out var qualified)
            || !_activeByPoint.TryGetValue(qualified, out var existing))
        {
            throw new ZeusException($"找不到标识为 {id} 的活动报警。请确认该报警尚未复归。");
        }

        return existing;
    }

    private PointAlarmRecord? FindByNameLocked(string key)
        => _activeByPoint.Values.FirstOrDefault(item =>
            item.QualifiedName.Equals(key, StringComparison.OrdinalIgnoreCase)
            || item.PointName.Equals(key, StringComparison.OrdinalIgnoreCase));

    private string ResolvePointKey(string pointName)
    {
        if (string.IsNullOrWhiteSpace(pointName))
        {
            throw new ZeusException("点名不能为空。");
        }

        var key = pointName.Trim();
        if (_points.TryGet(key, out var snapshot) && snapshot is not null)
        {
            return snapshot.QualifiedName;
        }

        return key;
    }

    private static PointAlarmRecord Clone(
        PointAlarmRecord existing,
        PointAlarmStatus status,
        object? value,
        DateTimeOffset? acknowledgedAt,
        DateTimeOffset? clearedAt,
        string? acknowledgedBy,
        string? assignee,
        DateTimeOffset? shelvedUntil,
        bool suppressed,
        PointAlarmState? alarmState = null,
        PointAlarmSeverity? severity = null,
        string? area = null)
        => new(
            existing.Id,
            existing.QualifiedName,
            existing.PointName,
            existing.DeviceName,
            alarmState ?? existing.AlarmState,
            status,
            value,
            existing.RaisedAt,
            acknowledgedAt,
            clearedAt,
            acknowledgedBy,
            severity ?? existing.Severity,
            area ?? existing.Area,
            assignee,
            shelvedUntil,
            suppressed);
}
