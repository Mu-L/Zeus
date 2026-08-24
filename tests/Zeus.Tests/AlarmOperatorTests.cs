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
