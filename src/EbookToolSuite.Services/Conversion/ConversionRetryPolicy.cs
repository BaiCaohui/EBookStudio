using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Services.Conversion;

/// <summary>
/// 重试判定（执行计划 T2-4 第 4 条）。纯逻辑，不碰进程也不碰数据库，所以能单测。
/// 判据是"这类失败再试一次有没有意义"：
///   磁盘满     -> 再试一次还是满，而且重试期间还继续占空间，绝不重试
///   参数错误   -> 参数不会自己变对，绝不重试
///   文件被占用 -> 占用会自己释放，必须重试（这是重试存在的主要理由）
///   超时       -> 再给一次机会，但只给一次（大书超时通常是真的慢，不是抖动）
/// </summary>
public static class ConversionRetryPolicy
{
    /// <summary>能重试时返回 true。调用方负责 RetryCount 自增、退避与重新入队。</summary>
    public static bool ShouldRetry(
        ErrorCode code, string? message, int retryCount, ConversionQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return code switch
        {
            // 不会自愈：重试只会浪费时间与磁盘
            ErrorCode.DiskFull => false,
            ErrorCode.ConversionUnsupported => false,
            ErrorCode.ConversionArgsRejected => false,
            ErrorCode.ConversionOutputExists => false,
            ErrorCode.FileNotFound => false,
            ErrorCode.CorruptFile => false,
            ErrorCode.ConversionCanceled => false,
            ErrorCode.Canceled => false,

            // 超时单独计数：只给一次额外机会
            ErrorCode.ProcessTimeout => retryCount < options.TimeoutMaxRetries,

            // 进程崩了 / 退出码非 0 / 没产出：只有"像是被占用"才值得重试
            ErrorCode.ProcessCrashed or ErrorCode.ConversionFailed or ErrorCode.ConversionNoOutput
                => retryCount < options.MaxRetries && LooksTransient(message),

            _ => false,
        };
    }

    /// <summary>
    /// 这条错误像不像"文件被占用"这类会自己好的问题。
    /// Calibre 是 Python 程序，报错文案中英混杂，所以中英各收几种说法。
    /// </summary>
    public static bool LooksTransient(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        foreach (var needle in TransientMarkers)
        {
            if (message.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>会自己好的失败，常见说法（Calibre 的 Python traceback 与 Win32 文案）。</summary>
    private static readonly string[] TransientMarkers =
    [
        "Permission denied",
        "Access is denied",
        "being used by another process",
        "The process cannot access the file",
        "sharing violation",
        "拒绝访问",
        "正在被另一进程使用",
        "另一个程序正在使用",
        "文件被占用",
        "共享冲突",
    ];
}
