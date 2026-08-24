using System.Windows;

namespace Zeus;

/// <summary>
/// 将 Zeus 宿主挂到 WPF。默认仍跟窗口寿命；登录后进主界面或托盘常驻请用 <see cref="UiHostAttachMode.Manual"/>。
/// </summary>
public static class WpfHostExtensions
{
    /// <summary>
    /// 把已创建的宿主交给窗口。默认在 Loaded 启动、Closed 释放。
    /// </summary>
    /// <param name="window">主窗口。手动模式可为 <c>null</c> 以外的任意窗口，仅用于启动失败弹框。</param>
    /// <param name="host">尚未启动的宿主。</param>
    /// <param name="mode">窗口寿命或手动启停。</param>
    public static UiHostAttachment AttachZeus(
        this Window window,
        IZeusHost host,
        UiHostAttachMode mode = UiHostAttachMode.WindowLifetime)
    {
        ArgumentNullException.ThrowIfNull(window);
        var attachment = new UiHostAttachment(host);
        if (mode == UiHostAttachMode.Manual)
        {
            return attachment;
        }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await attachment.StartAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    window,
                    ex.Message,
                    "Zeus 宿主启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        void OnClosed(object? sender, EventArgs e)
        {
            window.Loaded -= OnLoaded;
            window.Closed -= OnClosed;
            attachment.DisposeBlocking();
        }

        window.Loaded += OnLoaded;
        window.Closed += OnClosed;

        if (window.IsLoaded)
        {
            OnLoaded(window, new RoutedEventArgs());
        }

        return attachment;
    }

    /// <summary>
    /// 在窗口内创建并挂接宿主。
    /// </summary>
    public static UiHostAttachment AttachZeus(
        this Window window,
        Action<ZeusHostBuilder>? configure,
        UiHostAttachMode mode = UiHostAttachMode.WindowLifetime)
        => window.AttachZeus(ZeusHost.Create(configure), mode);
}
