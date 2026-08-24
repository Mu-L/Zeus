using System.Windows;
using System.Windows.Threading;

namespace Zeus;

/// <summary>
/// 基于 WPF <see cref="Dispatcher"/> 的调度器。
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <summary>
    /// 使用指定调度器。通常传入 <c>Application.Current.Dispatcher</c> 或控件的 <c>Dispatcher</c>。
    /// </summary>
    /// <param name="dispatcher">WPF 调度器。</param>
    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <inheritdoc />
    public bool CheckAccess() => _dispatcher.CheckAccess();

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// 当前应用程序的 UI 调度器。ViewModel 可在不持有控件时使用本方法。
    /// </summary>
    public static WpfUiDispatcher Current()
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        return new WpfUiDispatcher(dispatcher);
    }
}
