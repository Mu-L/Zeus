using Zeus;

namespace Zeus.Tests;

/// <summary>
/// 验证报警等级、责任人、搁置与抑制。这些是操作员队列能力，不是高低报方向。
/// </summary>
public sealed class AlarmOperatorTests
{
    /// <summary>
    /// 点上声明的严重等级与默认责任人应出现在活动记录上。
    /// </summary>
    [Fact]
    public void AlarmTable_CopiesSeverityAreaAndAssigneeFromLimits()
    {
        var table = new PointTable();
        var alarms = new PointAlarmTable(table);
        table.Register(new PointDefinition(
            "pv",
            "oven",
            PointValueKind.Double,
            new PointAlarmLimits(high: 80, severity: PointAlarmSeverity.Critical, area: "窑炉", defaultAssignee: "班长")));

        table.Publish("oven.pv", 90d);
        var record = Assert.Single(alarms.Active);
        Assert.Equal(PointAlarmSeverity.Critical, record.Severity);
        Assert.Equal("窑炉", record.Area);
        Assert.Equal("班长", record.Assignee);
    }

    /// <summary>
    /// 报警观察者抛异常不能阻止其他绑定源收到同一次报警变化。
    /// </summary>
    [Fact]
    public void AlarmTableChangedHandlerException_DoesNotSkipOtherSubscribers()
    {
        var table = new PointTable();
        var alarms = new PointAlarmTable(table);
        table.Register(new PointDefinition("pv", "oven", PointValueKind.Double, new PointAlarmLimits(high: 10)));
        var seen = 0;
        alarms.Changed += (_, _) => throw new InvalidOperationException("observer failed");
        alarms.Changed += (_, e) =>
        {
            seen++;
            Assert.Equal("oven.pv", e.Current.QualifiedName);
        };

        table.Publish("oven.pv", 20d);

        Assert.Equal(1, seen);
    }

    /// <summary>
    /// 指派会改责任人，不改变确认状态。
    /// </summary>
    [Fact]
    public void AlarmTable_AssignUpdatesAssignee()
    {
        var table = new PointTable();
        var alarms = new PointAlarmTable(table);
        table.Register(new PointDefinition("pv", "oven", PointValueKind.Double, new PointAlarmLimits(high: 10)));
        table.Publish("oven.pv", 20d);

        var assigned = alarms.Assign(alarms.Active[0].Id, "甲");
        Assert.Equal("甲", assigned.Assignee);
        Assert.Equal(PointAlarmStatus.Active, assigned.Status);
        Assert.Equal("甲", alarms.Active[0].Assignee);
    }

    /// <summary>
    /// 搁置后不出现在活动列表；解除搁置后若仍越限则回来。
    /// </summary>
    [Fact]
    public void AlarmTable_ShelveHidesUntilUnshelved()
    {
        var table = new PointTable();
        var alarms = new PointAlarmTable(table);
        table.Register(new PointDefinition("pv", "oven", PointValueKind.Double, new PointAlarmLimits(high: 10)));
        table.Publish("oven.pv", 20d);
        var id = alarms.Active[0].Id;

        alarms.Shelve(id, DateTimeOffset.Now.AddMinutes(5));
        Assert.Empty(alarms.Active);

        alarms.Unshelve(id);
        Assert.Single(alarms.Active);
        Assert.Equal(PointAlarmStatus.Active, alarms.Active[0].Status);
    }

    /// <summary>
    /// 抑制期间越限不进活动队列；解除抑制后当前越限会再产生。
    /// </summary>
    [Fact]
    public void AlarmTable_SuppressIgnoresNewAlarms()
    {
        var table = new PointTable();
        var alarms = new PointAlarmTable(table);
        table.Register(new PointDefinition("pv", "oven", PointValueKind.Double, new PointAlarmLimits(high: 10)));
        alarms.Suppress("oven.pv");
        table.Publish("oven.pv", 20d);
        Assert.Empty(alarms.Active);
        Assert.True(alarms.IsSuppressed("oven.pv"));

        alarms.Unsuppress("oven.pv");
        table.Publish("oven.pv", 21d);
        Assert.Single(alarms.Active);
    }

    /// <summary>
    /// 解除抑制时如果点当前仍越限，应立即生成活动报警，不必等待下一次采集刷新。
    /// </summary>
    [Fact]
    public void AlarmTable_UnsuppressRaisesCurrentAlarmImmediately()
    {
        var table = new PointTable();
        var alarms = new PointAlarmTable(table);
        table.Register(new PointDefinition("pv", "oven", PointValueKind.Double, new PointAlarmLimits(high: 10)));
        var changes = 0;
        alarms.Changed += (_, _) => changes++;

        alarms.Suppress("oven.pv");
        table.Publish("oven.pv", 20d);
        Assert.Empty(alarms.Active);

        alarms.Unsuppress("oven.pv");

        var alarm = Assert.Single(alarms.Active);
        Assert.Equal(PointAlarmStatus.Active, alarm.Status);
        Assert.True(changes > 0);
    }

    /// <summary>
    /// 点表尚未注册时按短名抑制，注册后仍应能用同一短名解除。
    /// </summary>
    [Fact]
    public void AlarmTable_UnsuppressRemovesPreRegisteredShortNameSuppression()
    {
        var table = new PointTable();
        var alarms = new PointAlarmTable(table);

        alarms.Suppress("pv");
        table.Register(new PointDefinition("pv", "oven", PointValueKind.Double, new PointAlarmLimits(high: 10)));
        table.Publish("oven.pv", 20d);

        Assert.Empty(alarms.Active);
        Assert.True(alarms.IsSuppressed("oven.pv"));

        alarms.Unsuppress("pv");

        Assert.False(alarms.IsSuppressed("oven.pv"));
        Assert.Single(alarms.Active);
    }

    /// <summary>
    /// 多台设备共享短名时，预注册短名抑制应能一次解除并恢复所有当前越限点。
    /// </summary>
    [Fact]
    public void AlarmTable_UnsuppressPreRegisteredAmbiguousShortNameRestoresAllMatchingAlarms()
    {
        var table = new PointTable();
        var alarms = new PointAlarmTable(table);

        alarms.Suppress("pv");
        table.Register(new PointDefinition("pv", "oven", PointValueKind.Double, new PointAlarmLimits(high: 10)));
        table.Register(new PointDefinition("pv", "dryer", PointValueKind.Double, new PointAlarmLimits(high: 10)));
        table.Publish("oven.pv", 20d);
        table.Publish("dryer.pv", 30d);

        Assert.Empty(alarms.Active);
        Assert.True(alarms.IsSuppressed("oven.pv"));
        Assert.True(alarms.IsSuppressed("dryer.pv"));

        alarms.Unsuppress("pv");

        Assert.False(alarms.IsSuppressed("oven.pv"));
        Assert.False(alarms.IsSuppressed("dryer.pv"));
        Assert.Equal(2, alarms.Active.Count);
    }

    /// <summary>
    /// 对已有报警解除抑制也要发布 Changed，否则绑定源不会刷新活动队列。
    /// </summary>
    [Fact]
    public void AlarmTable_UnsuppressExistingAlarmRaisesChanged()
    {
        var table = new PointTable();
        var alarms = new PointAlarmTable(table);
        table.Register(new PointDefinition("pv", "oven", PointValueKind.Double, new PointAlarmLimits(high: 10)));
        table.Publish("oven.pv", 20d);
        alarms.Suppress("oven.pv");
        var changed = false;
        alarms.Changed += (_, e) => changed = e.Current is { Status: PointAlarmStatus.Active, Suppressed: false };

        alarms.Unsuppress("oven.pv");

        Assert.True(changed);
        Assert.Single(alarms.Active);
    }

    /// <summary>
    /// 搁置到期应主动通知绑定层刷新，不要求 UI 主动轮询 Active。
    /// </summary>
    [Fact]
    public async Task AlarmTable_ShelveExpiryRaisesChanged()
    {
        var table = new PointTable();
        var alarms = new PointAlarmTable(table);
        table.Register(new PointDefinition("pv", "oven", PointValueKind.Double, new PointAlarmLimits(high: 10)));
        table.Publish("oven.pv", 20d);
        var id = alarms.Active[0].Id;
        var restored = new TaskCompletionSource<PointAlarmRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        alarms.Changed += (_, e) =>
        {
            if (e.Current.Status == PointAlarmStatus.Active)
            {
                restored.TrySetResult(e.Current);
            }
        };

        alarms.Shelve(id, DateTimeOffset.Now.AddMilliseconds(80));

        var record = await restored.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(id, record.Id);
        Assert.Single(alarms.Active);
    }

    /// <summary>
    /// 协议内部超时应映射成协议异常；调用方取消仍保持取消语义。
    /// </summary>
    [Fact]
    public void ProtocolTimeout_DistinguishesTimeoutFromCallerCancellation()
    {
        using var timeout = new CancellationTokenSource();
        timeout.Cancel();

        var protocolError = Assert.Throws<ZeusProtocolException>(() =>
            ProtocolTimeout.ThrowIfCancellationRequested(
                timeout.Token,
                CancellationToken.None,
                () => new ZeusProtocolException("超时")));
        Assert.Equal("超时", protocolError.Message);

        using var caller = new CancellationTokenSource();
        caller.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ProtocolTimeout.ThrowIfCancellationRequested(
                caller.Token,
                caller.Token,
                () => new ZeusProtocolException("超时")));
    }

    /// <summary>
    /// 整表绑定源在一轮批次结束时只通知一次。
    /// </summary>
    [Fact]
    public void PointTableBindingSource_RaisesOncePerBatch()
    {
        var table = new PointTable();
        table.Register(new PointDefinition("a", "oven", PointValueKind.UInt16));
        table.Register(new PointDefinition("b", "oven", PointValueKind.UInt16));
        using var source = new PointTableBindingSource(table, ImmediateUiDispatcher.Instance);
        var notifications = 0;
        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PointTableBindingSource.All))
            {
                notifications++;
            }
        };

        table.BeginBatch();
        table.Publish("oven.a", (ushort)1);
        table.Publish("oven.b", (ushort)2);
        table.EndBatch();

        Assert.Equal(1, notifications);
        Assert.Equal(2, source.Count);
    }

    /// <summary>
    /// 手动附件不会自动启动宿主。
    /// </summary>
    [Fact]
    public async Task UiHostAttachment_ManualDoesNotStart()
    {
        await using var host = ZeusHost.Create(builder => builder.AddVirtualChannel("bus"));
        var attachment = host.AttachManually();
        Assert.False(host.IsRunning);
        await attachment.StartAsync();
        Assert.True(host.IsRunning);
    }
}
