namespace Zeus;

/// <summary>
/// 与具体桌面框架无关的宿主挂接。登录后进主界面、托盘常驻请用本类型，不要绑窗口 Loaded。
/// </summary>
public static class UiHostExtensions
{
    /// <summary>
    /// 创建手动附件：不绑窗口。调用方在就绪后 <see cref="UiHostAttachment.StartAsync"/>，退出时释放。
    /// </summary>
    public static UiHostAttachment AttachManually(this IZeusHost host)
        => new(host);
}
