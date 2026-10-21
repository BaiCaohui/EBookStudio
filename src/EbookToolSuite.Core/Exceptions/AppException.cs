using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Exceptions;

/// <summary>
/// 应用内唯一的业务异常类型。携带错误码、面向用户的提示、以及可选的诊断上下文。
/// 上下文字典会被结构化日志原样记录，不要放敏感信息（口令、完整路径的用户名段等）。
/// </summary>
public sealed class AppException : Exception
{
    public ErrorCode Code { get; }

    /// <summary>面向用户的提示，须包含可执行建议。不要放堆栈或英文异常原文。</summary>
    public string UserMessage { get; }

    public IReadOnlyDictionary<string, object?> Context { get; }

    public AppException(
        ErrorCode code,
        string userMessage,
        string? technical = null,
        Exception? inner = null,
        IReadOnlyDictionary<string, object?>? context = null)
        : base(technical ?? userMessage, inner)
    {
        Code = code;
        UserMessage = userMessage;
        Context = context ?? new Dictionary<string, object?>();
    }
}