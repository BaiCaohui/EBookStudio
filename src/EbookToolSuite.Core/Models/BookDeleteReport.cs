namespace EbookToolSuite.Core.Models;

/// <summary>
/// 删除一本书的结果。
/// Warning 非空表示「书已经从书库移除，但有东西没删干净」（文件被占用等），
/// 这时 Removed 仍然是 true：用户要的是「从书库里消失」，文件残留只是善后问题。
/// </summary>
public sealed record BookDeleteOutcome(Guid BookId, string Title, bool Removed, string? Warning);

/// <summary>一次删除操作的逐本汇总。不抛异常表达业务失败，全部走这里。</summary>
public sealed record BookDeleteReport(IReadOnlyList<BookDeleteOutcome> Items)
{
    /// <summary>真正从书库删掉的本书。</summary>
    public int RemovedCount => Items.Count(i => i.Removed);

    /// <summary>删掉了、但留下残留文件的本书。</summary>
    public int WarningCount => Items.Count(i => i.Removed && i.Warning is not null);

    /// <summary>压根没删成的本数（读库或删库时报错）。</summary>
    public int FailedCount => Items.Count(i => !i.Removed);

    public bool AllRemoved => RemovedCount == Items.Count;

    /// <summary>给用户看的一句话总结。</summary>
    public string Summary
    {
        get
        {
            var text = FailedCount == Items.Count && Items.Count > 0
                ? "一本也没删掉。"
                : "已删除 " + RemovedCount + " 本。";

            if (WarningCount > 0)
            {
                text += "其中 " + WarningCount + " 本的文件没删干净（可能被占用）。";
            }

            if (FailedCount > 0)
            {
                text += "另有 " + FailedCount + " 本删除失败。";
            }

            return text;
        }
    }
}