namespace Zeus;

/// <summary>
/// 把宿主挂到界面时的生命周期策略。
/// 演示窗口可用窗口寿命；登录后进主界面、托盘常驻请用手动或应用程序寿命。
/// </summary>
public enum UiHostAttachMode
{
    /// <summary>
    /// 不自动启停。调用方在登录成功或主界面就绪后 <see cref="UiHostAttachment.StartAsync"/>，
    /// 在退出时 <see cref="UiHostAttachment.DisposeAsync"/>。
    /// </summary>
    Manual = 0,

    /// <summary>
    /// 窗口 Loaded / Load 时启动，Closed 时释放。适合单窗口演示，不适合多窗口或登录后再进主界面。
    /// </summary>
    WindowLifetime = 1
}
