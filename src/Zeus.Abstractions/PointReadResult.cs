namespace Zeus;

/// <summary>
/// 单个点的一次采集结果。协议层只报告结果，点表发布、批次事件和报警由宿主统一处理。
/// </summary>
public sealed class PointReadResult
{
    private PointReadResult(
        string qualifiedName,
        object? value,
        string? error,
        PointQuality quality,
        DateTimeOffset? sourceTimestamp)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            throw new ZeusException("采集结果的限定点名不能为空。");
        }

        QualifiedName = qualifiedName.Trim();
        Value = value;
        Error = string.IsNullOrWhiteSpace(error) ? null : error;
        Quality = quality;
        SourceTimestamp = sourceTimestamp;
    }

    /// <summary>限定点名，格式为 设备.点。</summary>
    public string QualifiedName { get; }

    /// <summary>采集成功时的值。</summary>
    public object? Value { get; }

    /// <summary>采集失败时的错误说明。</summary>
    public string? Error { get; }

    /// <summary>本次结果的数据质量。</summary>
    public PointQuality Quality { get; }

    /// <summary>数据源侧采样时间。为空时由点表使用本地接收时间。</summary>
    public DateTimeOffset? SourceTimestamp { get; }

    /// <summary>是否为成功结果。</summary>
    public bool IsSuccess => Error is null;

    /// <summary>创建成功结果。</summary>
    public static PointReadResult Success(
        string qualifiedName,
        object? value,
        DateTimeOffset? sourceTimestamp = null,
        PointQuality quality = PointQuality.Good)
        => new(qualifiedName, value, null, quality, sourceTimestamp);

    /// <summary>创建失败结果。</summary>
    public static PointReadResult Failure(
        string qualifiedName,
        string error,
        DateTimeOffset? sourceTimestamp = null,
        PointQuality quality = PointQuality.Bad)
        => new(qualifiedName, null, error, quality, sourceTimestamp);
}
