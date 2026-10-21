using System.IO;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;

namespace EbookToolSuite.Core.Errors;

/// <summary>
/// 把系统级 IO 异常翻译成 <see cref="AppException"/>（执行计划 T7-2 的 1/2/3 号边界）。
///
/// 【为什么不能直接抛 IOException】界面上只会看到
/// "System.IO.IOException: The device is not ready"；用户不知道该修什么。
/// 这里按 Windows 错误码（<see cref="Exception.HResult"/> 的低 16 位）分类，
/// 落到错误目录里的中文文案与建议动作上。
/// </summary>
public static class FileErrors
{
    /// <summary>
    /// 翻译一个异常。<paramref name="what"/> 是"正在做什么"（如"保存《书名》"），
    /// 会拼在文案最前面，让提示完整：发生了什么 + 为什么 + 怎么办。
    /// 已经是 <see cref="AppException"/> 的原样返回，不重复包装。
    /// </summary>
    public static AppException Translate(Exception ex, string what, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex is AppException already)
        {
            return already;
        }

        var info = Classify(ex);
        var context = new Dictionary<string, object?>
        {
            ["what"] = what,
            ["exception"] = ex.GetType().Name,
        };

        if (!string.IsNullOrEmpty(path))
        {
            context["path"] = path;
        }

        var userMessage = string.IsNullOrWhiteSpace(what)
            ? info.Message + info.Action
            : $"{what}：{info.Message}{info.Action}";

        return new AppException(
            info.Code,
            userMessage,
            technical: ex.GetType().Name + ": " + ex.Message,
            inner: ex,
            context: context);
    }

    /// <summary>只分类，不包装。返回的文案来自 <see cref="ErrorCatalog"/>。</summary>
    public static ErrorInfo Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex switch
        {
            UnauthorizedAccessException => ErrorCatalog.Get(ErrorCode.AccessDenied),
            FileNotFoundException => ErrorCatalog.Get(ErrorCode.FileNotFound),
            DirectoryNotFoundException => ErrorCatalog.Get(ErrorCode.FileNotFound),
            PathTooLongException => ErrorCatalog.Get(ErrorCode.InvalidPath),
            IOException io => ByWin32(io.HResult),
            _ => ErrorCatalog.Get(ErrorCode.InternalError),
        };
    }

    /// <summary>按 Win32 错误码分类（IOException.HResult 的低 16 位）。</summary>
    public static ErrorInfo ByWin32(int hresult)
    {
        var code = hresult & 0xFFFF;

        return code switch
        {
            // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
            32 or 33 => ErrorCatalog.Get(ErrorCode.FileLocked),

            // ERROR_HANDLE_DISK_FULL / ERROR_DISK_FULL
            39 or 112 => ErrorCatalog.Get(ErrorCode.DiskFull),

            // ERROR_NOT_READY（盘符消失 / 可移动盘被拔掉）、
            // ERROR_BAD_NETPATH / ERROR_NETNAME_DELETED / ERROR_NETWORK_UNREACHABLE / ERROR_CONNECTION_INVALID
            21 or 53 or 64 or 67 or 1231 => ErrorCatalog.Get(ErrorCode.DataDirNotWritable),

            // ERROR_ACCESS_DENIED / ERROR_WRITE_PROTECT
            5 or 19 => ErrorCatalog.Get(ErrorCode.AccessDenied),

            // ERROR_PATH_NOT_FOUND
            3 => ErrorCatalog.Get(ErrorCode.FileNotFound),

            _ => ErrorCatalog.Get(ErrorCode.InternalError),
        };
    }

    /// <summary>
    /// 把异常直接变成一句可以显示的中文。UI 的兜底 catch 用它，
    /// 保证 <c>ex.Message</c>（可能是英文）不会漏到界面上。
    /// </summary>
    public static string UserMessage(Exception ex, string what)
    {
        if (ex is AppException app)
        {
            return app.UserMessage;
        }

        if (ex is OperationCanceledException)
        {
            return ErrorCatalog.Get(ErrorCode.Canceled).Message;
        }

        return Translate(ex, what).UserMessage;
    }
}
