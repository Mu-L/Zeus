namespace Zeus;

/// <summary>
/// 协议等待循环的超时映射工具。区分调用方取消与内部应答超时，避免把超时漏成裸取消异常。
/// </summary>
internal static class ProtocolTimeout
{
    /// <summary>
    /// 检查组合超时令牌；如果是协议内部超时，则抛出调用方提供的协议异常。
    /// </summary>
    public static void ThrowIfCancellationRequested(
        CancellationToken timeoutToken,
        CancellationToken cancellationToken,
        Func<Exception> timeoutExceptionFactory)
    {
        ArgumentNullException.ThrowIfNull(timeoutExceptionFactory);
        try
        {
            timeoutToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw timeoutExceptionFactory();
        }
    }
}
