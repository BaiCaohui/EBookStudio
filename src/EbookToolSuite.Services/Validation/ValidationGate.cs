using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Services.Validation;

/// <summary>
/// EpubCheck 结果 -> 保存门禁结论。**纯函数**：不碰文件、不碰进程、不碰 UI，
/// 所以策略表能一条条单测（《分阶段执行计划》T3-5 的分级门禁表）。
///
/// 策略（2026-10 定稿，与计划原文一致）：
///   fatal=0 error=0  -> 静默通过
///   只有 warning     -> 放行 + 提示"有 N 条警告"
///   有 error/fatal   -> 阻止覆盖，但界面给「仍然保存（不推荐）」
///   超时             -> 不硬拦，界面给「跳过校验保存」
///   工具跑挂/报告坏   -> 同上（不敢当通过，但也别把用户卡死）
///   没装工具         -> 直接放行 + 提示"已跳过校验"
/// </summary>
public static class ValidationGate
{
    public const string ToolMissingSummary = "未安装校验工具（JRE 或 EpubCheck 缺失），已跳过校验。";

    /// <summary>报告 -> 结论。</summary>
    public static ValidationDecision Decide(CheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        // 报告读不出来：既不能当通过，也不该硬拦（文件可能完全没问题），交给用户拍板。
        if (report.ParseFailed)
        {
            return new ValidationDecision(
                ValidationOutcome.ToolFailed,
                "EpubCheck 的报告读不出来，没法定夺：" + (report.ParseFailureReason ?? "未知原因"),
                Array.Empty<CheckMessage>());
        }

        if (report.HasBlockingErrors)
        {
            return new ValidationDecision(
                ValidationOutcome.Blocked,
                report.FatalCount > 0
                    ? "EpubCheck 报出 " + report.FatalCount + " 个致命错误、" + report.ErrorCount + " 个错误，保存会破坏文件。"
                    : "EpubCheck 报出 " + report.ErrorCount + " 个错误，保存会破坏文件。",
                report.Messages);
        }

        if (report.HasWarnings)
        {
            return new ValidationDecision(
                ValidationOutcome.PassWithWarnings,
                "EpubCheck 有 " + report.WarningCount + " 条警告，没有错误，可以保存。",
                report.Messages);
        }

        return new ValidationDecision(
            ValidationOutcome.Pass,
            "EpubCheck 校验通过（" + report.CountsText + "）。",
            report.Messages);
    }

    /// <summary>校验超时（ProcessHost 抛 ProcessTimeout 时用）。</summary>
    public static ValidationDecision Timeout()
        => new(
            ValidationOutcome.TimedOut,
            "校验超时，没能确认文件是否合规。可以跳过校验直接保存，也可以取消后再等等看。",
            Array.Empty<CheckMessage>());

    /// <summary>没装 JRE / EpubCheck。</summary>
    public static ValidationDecision ToolMissing(string? reason)
        => new(
            ValidationOutcome.ToolMissing,
            string.IsNullOrWhiteSpace(reason) ? ToolMissingSummary : ToolMissingSummary + "（" + reason + "）",
            Array.Empty<CheckMessage>());

    /// <summary>工具跑挂了（退出码异常等）。</summary>
    public static ValidationDecision ToolFailed(string? reason)
        => new(
            ValidationOutcome.ToolFailed,
            "校验工具运行失败，没能确认文件是否合规。" + (string.IsNullOrWhiteSpace(reason) ? string.Empty : "（" + reason + "）"),
            Array.Empty<CheckMessage>());
}
