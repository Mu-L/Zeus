using System.Windows.Forms;

namespace Zeus;

/// <summary>
/// 将 Zeus 宿主挂到 WinForms 窗口生命周期：Load 时启动，FormClosed 时释放。
/// </summary>
public static class WinFormsHostExtensions
{
    /// <summary>
    /// 把已创建的宿主交给窗体管理。返回值可用于查找通道；不必再手写启停。
    /// </summary>
    /// <param name="form">主窗体或承载宿主的容器窗体。</param>
    /// <param name="host">尚未启动的宿主。</param>
    /// <param name="mode">窗口寿命或手动启停。默认跟窗体 Load / FormClosed。</param>
    /// <returns>宿主附件，<see cref="UiHostAttachment.Host"/> 即原宿主。</returns>
    public static UiHostAttachment AttachZeus(
        this Form form,
        IZeusHost host,
        UiHostAttachMode mode = UiHostAttachMode.WindowLifetime)
    {
        ArgumentNullException.ThrowIfNull(form);
        var attachment = new UiHostAttachment(host);
        if (mode == UiHostAttachMode.Manual)
        {
            return attachment;
        }

        async void OnLoad(object? sender, EventArgs e)
        {
            try
            {
                await attachment.StartAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (!form.IsDisposed)
                {
                    MessageBox.Show(
                        form,
                        ex.Message,
                        "Zeus 宿主启动失败",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        void OnClosed(object? sender, EventArgs e)
        {
            form.Load -= OnLoad;
            form.FormClosed -= OnClosed;
            attachment.DisposeBlocking();
        }

        form.Load += OnLoad;
        form.FormClosed += OnClosed;

        if (form.IsHandleCreated)
        {
            OnLoad(form, EventArgs.Empty);
        }

        return attachment;
    }

    /// <summary>
    /// 在窗体内创建并挂接宿主，等价于 <c>AttachZeus(ZeusHost.Create(...))</c>。
    /// </summary>
    /// <param name="form">主窗体。</param>
    /// <param name="configure">通道与设备注册。</param>
    /// <param name="mode">窗口寿命或手动启停。</param>
    public static UiHostAttachment AttachZeus(
        this Form form,
        Action<ZeusHostBuilder>? configure,
        UiHostAttachMode mode = UiHostAttachMode.WindowLifetime)
        => form.AttachZeus(ZeusHost.Create(configure), mode);
}
