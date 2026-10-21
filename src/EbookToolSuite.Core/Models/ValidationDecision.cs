namespace EbookToolSuite.Core.Models;

/// <summary>门禁结论的档位（策略表见《分阶段执行计划》T3-5）。</summary>
public enum ValidationOutcome
{
    /// <summary>0 错误 0 警告：静默通过。</summary>
    Pass,

    /// <summary>只有警告：放行，但界面要黄色提示「有 N 条警告」。</summary>
    PassWithWarnings,

    /// <summary>有 fatal/error：默认阻止保存，但界面给「仍然保存（不推荐）」。</summary>
    Blocked,

    /// <summary>校验超时：界面给「跳过校验保存」。</summary>
    TimedOut,

    /// <summary>工具跑挂了或报告读不出来：同上（不敢当通过，但也不硬拦）。</summary>
    ToolFailed,

    /// <summary>没装 JRE/EpubCheck：直接放行 + 提示「已跳过校验」。</summary>
    ToolMissing,
}

/// <summary>
/// EpubCheck 结果 -> 能不能覆盖原文件 的结论。
/// 由 ValidationGate 这个纯函数产出，界面只负责把结论翻译成提示与按钮。
/// </summary>
public sealed record ValidationDecision(
    ValidationOutcome Outcome,
    string Summary,
    IReadOnlyList<CheckMessage> Messages)
{
    /// <summary>需要用户在界面上再点一次（阻止 / 超时 / 工具挂了）。</summary>
    public bool NeedsConfirmation
        => Outcome is ValidationOutcome.Blocked
            or ValidationOutcome.TimedOut
            or ValidationOutcome.ToolFailed;

    /// <summary>不用问用户，直接保存（通过 / 只有警告 / 没装工具）。</summary>
    public bool IsSilentPass
        => Outcome is ValidationOutcome.Pass
            or ValidationOutcome.PassWithWarnings
            or ValidationOutcome.ToolMissing;

    /// <summary>真的拦住了（有 fatal/error）。</summary>
    public bool IsBlocked => Outcome == ValidationOutcome.Blocked;

    /// <summary>确认按钮上的字（计划的口径：blocked 给「仍然保存（不推荐）」，其余给「跳过校验保存」）。</summary>
    public string ConfirmActionText
        => Outcome == ValidationOutcome.Blocked ? "仍然保存（不推荐）" : "跳过校验保存";

    /// <summary>界面上最多显示几行 message。</summary>
    public IReadOnlyList<string> TopMessages(int max = 12)
    {
        if (max <= 0 || Messages.Count == 0)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>(Math.Min(max, Messages.Count));
        foreach (var message in Messages)
        {
            if (lines.Count >= max)
            {
                break;
            }

            lines.Add(message.DisplayText);
        }

        return lines;
    }
}
