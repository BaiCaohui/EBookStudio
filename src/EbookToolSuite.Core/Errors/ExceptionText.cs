using EbookToolSuite.Core.Exceptions;

namespace EbookToolSuite.Core.Errors;

/// <summary>
/// 面向界面的异常文案入口（执行计划 T7-2 判据 2）。
///
/// 【为什么必须走这里】<see cref="Exception.Message"/> 对 <c>AppException</c> 可能装的是
/// technical（英文上下文），对系统异常则是英文原文。界面上直接拼 <c>ex.Message</c>
/// 就等于把英文给用户看。这个扩展统一收口：业务异常取 UserMessage，其余先按错误码翻译。
/// </summary>
public static class ExceptionText
{
    /// <summary>把任意异常变成可以直接显示的一句话（中文，含建议动作）。</summary>
    public static string SafeMessage(this Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex switch
        {
            AppException app => app.UserMessage,
            OperationCanceledException => FileErrors.UserMessage(ex, string.Empty),
            _ => FileErrors.Translate(ex, string.Empty).UserMessage,
        };
    }
}
