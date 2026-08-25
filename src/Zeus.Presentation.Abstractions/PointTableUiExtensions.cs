namespace Zeus;

/// <summary>
/// 把点表变化封送到界面。释放绑定句柄即退订。
/// </summary>
public static class PointTableUiExtensions
{
    /// <summary>
    /// 把指定点的完整快照推到界面，订阅时会立即推送当前快照。
    /// </summary>
    /// <param name="table">宿主点表。</param>
    /// <param name="pointName">短名或 <c>设备.点</c>。</param>
    /// <param name="dispatcher">界面调度器。</param>
    /// <param name="setSnapshot">在界面线程上接收快照。</param>
    /// <returns>绑定句柄。</returns>
    public static IUiBinding BindSnapshot(
        this IPointTable table,
        string pointName,
        IUiDispatcher dispatcher,
        Action<PointSnapshot> setSnapshot)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(setSnapshot);
        var key = PointUiFormatting.NormalizePointName(pointName);

        void Apply(PointSnapshot snapshot)
        {
            if (dispatcher.CheckAccess())
            {
                setSnapshot(snapshot);
                return;
            }

            dispatcher.Post(() => setSnapshot(snapshot));
        }

        void OnChanged(object? sender, PointChangedEventArgs e)
        {
            Apply(e.Current);
        }

        if (table.TryGet(key, out var existing) && existing is not null)
        {
            Apply(existing);
        }

        var subscription = table.Subscribe(key, OnChanged);
        return new DelegateUiBinding(subscription.Dispose);
    }

    /// <summary>
    /// 把指定点的当前值按 0–1 比例推到界面，适合进度条或仪表指针。
    /// 默认用报警限作为量程；未配置报警限时需传入 <paramref name="minimum"/> 与 <paramref name="maximum"/>。
    /// </summary>
    public static IUiBinding BindGauge(
        this IPointTable table,
        string pointName,
        IUiDispatcher dispatcher,
        Action<double> setRatio,
        double? minimum = null,
        double? maximum = null)
    {
        ArgumentNullException.ThrowIfNull(setRatio);
        return table.BindSnapshot(pointName, dispatcher, snapshot =>
        {
            if (!TryToDouble(snapshot.Value, out var value))
            {
                setRatio(0);
                return;
            }

            var low = minimum ?? snapshot.Definition.AlarmLimits?.Low ?? 0;
            var high = maximum ?? snapshot.Definition.AlarmLimits?.High ?? (low + 100);
            if (high <= low)
            {
                setRatio(0);
                return;
            }

            var ratio = (value - low) / (high - low);
            if (ratio < 0)
            {
                ratio = 0;
            }
            else if (ratio > 1)
            {
                ratio = 1;
            }

            setRatio(ratio);
        });
    }

    /// <summary>
    /// 把指定点的值格式化为文本并推到界面。点尚无值时先推送空字符串。
    /// </summary>
    /// <param name="table">宿主点表。</param>
    /// <param name="pointName">短名或 <c>设备.点</c>。</param>
    /// <param name="dispatcher">界面调度器。</param>
    /// <param name="setText">在界面线程上设置文本。</param>
    /// <param name="formatter">可选格式化；默认使用不变区域性的 <see cref="object.ToString"/>。</param>
    /// <returns>绑定句柄。</returns>
    public static IUiBinding BindText(
        this IPointTable table,
        string pointName,
        IUiDispatcher dispatcher,
        Action<string> setText,
        Func<object?, string>? formatter = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(setText);
        formatter ??= PointUiFormatting.DefaultFormat;
        return table.BindSnapshot(
            pointName,
            dispatcher,
            snapshot => setText(PointUiFormatting.FormatSnapshot(snapshot, formatter)));
    }

    /// <summary>
    /// 按点快照控制界面元素启用状态，订阅时会立即推送当前状态。默认仅可写且无错误时启用。
    /// </summary>
    /// <param name="table">宿主点表。</param>
    /// <param name="pointName">短名或 <c>设备.点</c>。</param>
    /// <param name="dispatcher">界面调度器。</param>
    /// <param name="setEnabled">在界面线程上设置启用状态。</param>
    /// <param name="isEnabled">快照到启用状态的映射；为空时使用可写且无错误。</param>
    /// <returns>绑定句柄。</returns>
    public static IUiBinding BindEnabled(
        this IPointTable table,
        string pointName,
        IUiDispatcher dispatcher,
        Action<bool> setEnabled,
        Func<PointSnapshot, bool>? isEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(setEnabled);
        isEnabled ??= static snapshot => snapshot.Definition.Writable && snapshot.Error is null;
        return table.BindSnapshot(pointName, dispatcher, snapshot => setEnabled(isEnabled(snapshot)));
    }

    /// <summary>
    /// 创建单个点的可绑定投影，属性变更会封送到 <paramref name="dispatcher"/>。
    /// </summary>
    /// <param name="table">宿主点表。</param>
    /// <param name="pointName">短名或 <c>设备.点</c>。</param>
    /// <param name="dispatcher">界面调度器。测试可传入 <see cref="ImmediateUiDispatcher"/>。</param>
    /// <param name="formatter">成功值到文本的转换；为空时使用不变区域性的 <see cref="object.ToString"/>。</param>
    public static PointBindingSource AsBindingSource(
        this IPointTable table,
        string pointName,
        IUiDispatcher? dispatcher = null,
        Func<object?, string>? formatter = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new PointBindingSource(table, pointName, dispatcher, formatter);
    }

    /// <summary>
    /// 创建整张点表的可绑定投影。ViewModel 应持有本对象，用 <see cref="SynchronizationContextUiDispatcher"/> 封送，不必再绑到某个控件。
    /// </summary>
    public static PointTableBindingSource AsTableBindingSource(this IPointTable table, IUiDispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new PointTableBindingSource(table, dispatcher);
    }

    /// <summary>
    /// 把报警队列变化推到界面，订阅时立即推送当前活动报警。
    /// </summary>
    public static IUiBinding BindAlarms(
        this IPointAlarmTable alarms,
        IUiDispatcher dispatcher,
        Action<IReadOnlyList<PointAlarmRecord>> setActive)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(setActive);

        void Apply(IReadOnlyList<PointAlarmRecord> records)
        {
            if (dispatcher.CheckAccess())
            {
                setActive(records);
                return;
            }

            dispatcher.Post(() => setActive(records));
        }

        void OnChanged(object? sender, PointAlarmChangedEventArgs e) => Apply(alarms.Active);

        Apply(alarms.Active);
        alarms.Changed += OnChanged;
        return new DelegateUiBinding(() => alarms.Changed -= OnChanged);
    }

    /// <summary>
    /// 创建报警队列的可绑定投影。
    /// </summary>
    public static PointAlarmBindingSource AsAlarmBindingSource(
        this IPointAlarmTable alarms,
        IUiDispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        return new PointAlarmBindingSource(alarms, dispatcher);
    }

    private static bool TryToDouble(object? value, out double result)
    {
        result = 0;
        if (value is null)
        {
            return false;
        }

        try
        {
            result = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return !double.IsNaN(result) && !double.IsInfinity(result);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
