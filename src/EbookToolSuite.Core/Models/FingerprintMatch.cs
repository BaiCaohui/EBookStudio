namespace EbookToolSuite.Core.Models;

/// <summary>
/// 预检去重时的一次命中：某个快速指纹命中了库里的哪本书（执行计划 T2-1）。
/// 带上 Title 是为了让重复对话框能直接显示"《书名》已在库中"，
/// 不用再为每一本重复单独查一次库。
/// </summary>
public sealed record FingerprintMatch(string QuickFingerprint, Guid BookId, string Title);