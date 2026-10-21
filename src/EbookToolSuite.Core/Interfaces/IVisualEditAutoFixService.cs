using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 可视编辑的"自动修正"执行链路（T5-2W 第 5 步 / 判据 9~12）。**只在用户点【确认修改】之后调用**。
/// </summary>
/// <remarks>
/// 固定顺序：建还原点（备份这一章修正前的原文）-> 应用修正 -> 再校验 -> 写工作区 -> 记 operation_log。
/// 修正后仍不合格时一个字节都不写（原文零改动），刚建的那个还原点会被清掉（原文没变，它没用了）。
/// 先建还原点再改盘的原因：改盘是覆盖写，覆盖了就回不去；宁可拦住保存，也不要"改了退不回去"。
/// </remarks>
public interface IVisualEditAutoFixService
{
    /// <summary>
    /// 执行一次自动修正。<paramref name="bodyInnerXml"/> 是编辑器回传的 body 内容（和保存时拿到的是同一份）。
    /// </summary>
    Task<VisualAutoFixOutcome> ApplyAsync(
        IEpubWorkspace workspace,
        string entry,
        string bodyInnerXml,
        CancellationToken ct = default);
}