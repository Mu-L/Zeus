namespace Zeus;

/// <summary>
/// 虚拟通道的对端。写入发生时由框架询问是否产生回包，用于无硬件联调协议。
/// </summary>
public interface IVirtualResponder
{
    /// <summary>
    /// 处理一次主机写入。可在实现中延时、丢包（返回 <c>null</c>）或响应取消。
    /// </summary>
    /// <param name="request">主机刚写入的原始字节。</param>
    /// <param name="cancellationToken">取消本次应答。</param>
    /// <returns>应回写到 <c>DataReceived</c> 的字节；不需要应答时返回 <c>null</c>。</returns>
    Task<ReadOnlyMemory<byte>?> RespondAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default);
}
