using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 章节源码的读写服务（执行计划 T5-2）：读出来给源码编辑器，校验通过才写回工作区。
/// </summary>
/// <remarks>
/// 这里只碰工作区（work/），不动原书：写进去的改动统一由"保存"那一步打包落盘。
/// 校验不过时<b>一个字节都不写</b>（判据 3 / 4）。
/// </remarks>
public interface IChapterSourceService
{
    /// <summary>读某章源码（原样返回，不改任何格式）。</summary>
    string ReadSource(IEpubWorkspace workspace, string entry);

    /// <summary>查一段源码能不能存（良构 XML + 有 html/body）。</summary>
    ChapterSourceCheck Check(string source);

    /// <summary>
    /// 保存源码：先校验，通过才写回工作区并记进会话的 changedFiles；
    /// 不通过原样返回带行号的结果，并且不写盘。
    /// </summary>
    ChapterSourceCheck Save(IEpubWorkspace workspace, string entry, string source);

    /// <summary>
    /// 可视模式保存（T5-2W）：把编辑器回传的 body 内容组装回整章 XHTML，校验通过才写盘。
    /// 校验不过时先给自动修正建议（<paramref name="applyFixes"/> 为 false 就只返回建议、不写盘）。
    /// </summary>
    ChapterVisualSaveResult SaveVisual(
        IEpubWorkspace workspace, string entry, string bodyInnerXml, bool applyFixes = false);
}
