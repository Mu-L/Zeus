using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Zeus;

/// <summary>
/// 整张点表的可绑定投影。属性变更封送到指定调度器，适合 MVVM：ViewModel 持有本对象，不必绑到某个控件。
/// 优先订阅 <see cref="IPointTable.BatchChanged"/>，一轮采集只刷新一次。
/// </summary>
public sealed class PointTableBindingSource : INotifyPropertyChanged, IDisposable
{
    private readonly IPointTable _table;
    private readonly IUiDispatcher _dispatcher;
    private IReadOnlyList<PointSnapshot> _all = Array.Empty<PointSnapshot>();
    private bool _disposed;

    /// <summary>
    /// 订阅点表并投影为可绑定属性。
    /// </summary>
    /// <param name="table">宿主点表。</param>
    /// <param name="dispatcher">属性变更发布所用的调度器。ViewModel 可传入 <see cref="SynchronizationContextUiDispatcher.Capture"/>。</param>
    public PointTableBindingSource(IPointTable table, IUiDispatcher? dispatcher = null)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _dispatcher = dispatcher ?? ImmediateUiDispatcher.Instance;
        Apply(_table.All, raiseChanged: false);
        _table.BatchChanged += OnBatchChanged;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前全部点快照，顺序与登记顺序一致。</summary>
    public IReadOnlyList<PointSnapshot> All => _all;

    /// <summary>已登记点数。</summary>
    public int Count => _all.Count;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _table.BatchChanged -= OnBatchChanged;
    }

    private void OnBatchChanged(object? sender, PointBatchChangedEventArgs e)
        => Dispatch(() => Apply(_table.All, raiseChanged: true));

    private void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Post(action);
    }

    private void Apply(IReadOnlyList<PointSnapshot> all, bool raiseChanged)
    {
        if (_disposed)
        {
            return;
        }

        _all = all.ToArray();
        if (!raiseChanged)
        {
            return;
        }

        OnPropertyChanged(nameof(All));
        OnPropertyChanged(nameof(Count));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
