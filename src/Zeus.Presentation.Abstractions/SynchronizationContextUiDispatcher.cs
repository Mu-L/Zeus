namespace Zeus;

/// <summary>
/// 基于 <see cref="SynchronizationContext"/> 的调度器。
/// ViewModel 在 UI 线程构造时捕获当前上下文，不必再拿一个控件去取 Dispatcher。
/// </summary>
public sealed class SynchronizationContextUiDispatcher : IUiDispatcher
{
    private readonly SynchronizationContext _context;
    private readonly int _threadId;

    /// <summary>
    /// 使用指定同步上下文。通常传入 <see cref="SynchronizationContext.Current"/>。
    /// </summary>
    /// <param name="context">界面线程的同步上下文。</param>
    public SynchronizationContextUiDispatcher(SynchronizationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _threadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// 捕获当前线程的同步上下文。必须在 UI 线程调用。
    /// </summary>
    public static SynchronizationContextUiDispatcher Capture()
    {
        var context = SynchronizationContext.Current;
        if (context is null)
        {
            throw new ZeusException(
                "当前线程没有 SynchronizationContext。请在 WPF / WinForms UI 线程上捕获，或显式传入 WpfUiDispatcher。");
        }

        return new SynchronizationContextUiDispatcher(context);
    }

    /// <inheritdoc />
    public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _context.Post(_ => action(), null);
    }
}
