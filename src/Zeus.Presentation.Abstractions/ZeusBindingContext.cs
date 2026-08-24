namespace Zeus;

/// <summary>
/// 把宿主上的通道、点表和报警投影成可绑定源。
/// 调度器只在创建时传入一次，后续 <see cref="Channel"/> / <see cref="Point"/> 都复用它。
/// </summary>
public sealed class ZeusBindingContext : IDisposable
{
    private readonly IZeusHost _host;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<IDisposable> _owned = [];
    private bool _disposed;

    /// <summary>
    /// 绑定到指定宿主与界面调度器。
    /// </summary>
    /// <param name="host">尚未或已经启动的宿主。</param>
    /// <param name="dispatcher">属性变更要封送到的界面线程。WPF 传入 <c>WpfUiDispatcher.Current()</c>。</param>
    public ZeusBindingContext(IZeusHost host, IUiDispatcher dispatcher)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>被投影的宿主。</summary>
    public IZeusHost Host => _host;

    /// <summary>本上下文使用的界面调度器。</summary>
    public IUiDispatcher Dispatcher => _dispatcher;

    /// <summary>
    /// 按名称投影一条通道。返回值由本上下文持有，调用 <see cref="Dispose"/> 时一并释放。
    /// </summary>
    /// <param name="name">通道名，须与注册名一致。</param>
    public ChannelBindingSource Channel(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = _host.Channels.Get(name).AsBindingSource(_dispatcher);
        _owned.Add(source);
        return source;
    }

    /// <summary>
    /// 按点名投影一个点。返回值由本上下文持有，调用 <see cref="Dispose"/> 时一并释放。
    /// </summary>
    /// <param name="name">短名或 <c>设备.点</c>。</param>
    /// <param name="formatter">成功值到文本的转换；为空时使用默认格式。</param>
    public PointBindingSource Point(string name, Func<object?, string>? formatter = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = _host.Points.AsBindingSource(name, _dispatcher, formatter);
        _owned.Add(source);
        return source;
    }

    /// <summary>
    /// 投影整张点表，按 <c>BatchChanged</c> 刷新。返回值由本上下文持有。
    /// </summary>
    public PointTableBindingSource Table()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = _host.Points.AsTableBindingSource(_dispatcher);
        _owned.Add(source);
        return source;
    }

    /// <summary>
    /// 投影活动报警队列。返回值由本上下文持有。
    /// </summary>
    public PointAlarmBindingSource Alarms()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = _host.Alarms.AsAlarmBindingSource(_dispatcher);
        _owned.Add(source);
        return source;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            _owned[i].Dispose();
        }

        _owned.Clear();
    }
}

/// <summary>
/// 宿主到绑定上下文的入口。调度器只在这里出现一次。
/// </summary>
public static class ZeusBindingContextExtensions
{
    /// <summary>
    /// 为当前宿主创建一个绑定上下文，后续通道和点都不必再传调度器。
    /// </summary>
    /// <param name="host">宿主。</param>
    /// <param name="dispatcher">界面调度器。</param>
    public static ZeusBindingContext Bind(this IZeusHost host, IUiDispatcher dispatcher)
        => new(host, dispatcher);
}
