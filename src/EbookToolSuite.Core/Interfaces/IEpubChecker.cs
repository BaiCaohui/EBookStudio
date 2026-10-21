using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// EPUB 校验器。当前实现是 EpubCheck 5.4.0 的进程封装（Engines/Epub/EpubCheckRunner.cs）。
///
/// 约定：校验器**不**决定能不能保存，它只负责把结果拿回来；
/// 能不能覆盖原文件由 Services/Validation/ValidationGate.cs 这个纯函数按级别决定。
/// </summary>
public interface IEpubChecker
{
    /// <summary>
    /// 校验一个 EPUB 文件（必须是磁盘上已经存在的完整文件，不要拿半成品来试）。
    /// 工具缺失/超时/跑挂时抛 AppException（MissingTool / JavaMissing / ProcessTimeout / ProcessCrashed），
    /// 报告本身读不出来时返回 ParseFailed=true 的 CheckReport（不抛）。
    /// </summary>
    Task<CheckReport> ValidateAsync(string epubPath, CancellationToken ct = default);
}
