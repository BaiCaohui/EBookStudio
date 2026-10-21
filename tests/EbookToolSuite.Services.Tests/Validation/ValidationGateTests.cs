using EbookToolSuite.Core.Models;
using EbookToolSuite.Services.Validation;

namespace EbookToolSuite.Services.Tests.Validation;

/// <summary>
/// 门禁策略表（《分阶段执行计划》T3-5 的分级门禁表）逐条钉死。
/// 这是纯函数，所以策略本身能被穷举测试界面上"到底拦不拦"完全由它说了算。
/// </summary>
public sealed class ValidationGateTests
{
    [Fact]
    public void 零错误零警告_静默通过()
    {
        var decision = ValidationGate.Decide(Report());

        Assert.Equal(ValidationOutcome.Pass, decision.Outcome);
        Assert.True(decision.IsSilentPass);
        Assert.False(decision.NeedsConfirmation);
        Assert.False(decision.IsBlocked);
    }

    [Fact]
    public void 只有警告_放行但要提示条数()
    {
        var decision = ValidationGate.Decide(Report(warning: 2));

        Assert.Equal(ValidationOutcome.PassWithWarnings, decision.Outcome);
        Assert.True(decision.IsSilentPass);
        Assert.False(decision.NeedsConfirmation);
        Assert.Contains("2", decision.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 有error_阻止但给仍然保存的选项()
    {
        var messages = new[] { Error("OPF-003", "manifest 里有未声明的文件", "content.opf", 12, 3) };

        var decision = ValidationGate.Decide(Report(error: 1, messages: messages));

        Assert.Equal(ValidationOutcome.Blocked, decision.Outcome);
        Assert.True(decision.IsBlocked);
        Assert.True(decision.NeedsConfirmation);
        Assert.False(decision.IsSilentPass);
        Assert.Equal("仍然保存（不推荐）", decision.ConfirmActionText);
        // message 要原样带给界面，让用户看到到底是哪一条
        Assert.Same(messages[0], Assert.Single(decision.Messages));
    }

    [Fact]
    public void 有fatal_也算阻止且摘要点出致命()
    {
        var decision = ValidationGate.Decide(Report(fatal: 1));

        Assert.Equal(ValidationOutcome.Blocked, decision.Outcome);
        Assert.Contains("致命", decision.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 报告读不出来_不硬拦但也不当通过()
    {
        var decision = ValidationGate.Decide(CheckReport.Unparseable("报告文件是空的"));

        Assert.Equal(ValidationOutcome.ToolFailed, decision.Outcome);
        Assert.False(decision.IsBlocked);
        Assert.True(decision.NeedsConfirmation);
        Assert.False(decision.IsSilentPass);
        Assert.Equal("跳过校验保存", decision.ConfirmActionText);
        Assert.Contains("报告文件是空的", decision.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 超时_给跳过校验保存的选项()
    {
        var decision = ValidationGate.Timeout();

        Assert.Equal(ValidationOutcome.TimedOut, decision.Outcome);
        Assert.True(decision.NeedsConfirmation);
        Assert.False(decision.IsBlocked);
        Assert.Equal("跳过校验保存", decision.ConfirmActionText);
    }

    [Fact]
    public void 工具缺失_直接放行只提示()
    {
        var decision = ValidationGate.ToolMissing(null);

        Assert.Equal(ValidationOutcome.ToolMissing, decision.Outcome);
        Assert.True(decision.IsSilentPass);
        Assert.False(decision.NeedsConfirmation);
        Assert.Contains("跳过校验", decision.Summary, StringComparison.Ordinal);
        // 带原因时原因也要露出来，方便用户去装工具
        Assert.Contains("Tools/JRE", ValidationGate.ToolMissing("Tools/JRE 里没有 java.exe").Summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 工具跑挂_不硬拦且要问用户()
    {
        var decision = ValidationGate.ToolFailed("退出码 3");

        Assert.Equal(ValidationOutcome.ToolFailed, decision.Outcome);
        Assert.True(decision.NeedsConfirmation);
        Assert.Contains("退出码 3", decision.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 报告为空对象时_抛参数异常()
        => Assert.Throws<ArgumentNullException>(() => ValidationGate.Decide(null!));

    [Fact]
    public void 面板文本最多十二行_多余的截掉()
    {
        var messages = new CheckMessage[20];
        for (var i = 0; i < messages.Length; i++)
        {
            messages[i] = Error("OPF-00" + i, "第 " + i + " 条", "content.opf", i + 1, 1);
        }

        var decision = ValidationGate.Decide(Report(error: 20, messages: messages));

        Assert.Equal(12, decision.TopMessages().Count);
        Assert.Equal(20, decision.Messages.Count);
        Assert.Empty(ValidationGate.Decide(Report()).TopMessages());
    }

    // ---------- 辅助 ----------

    private static CheckReport Report(
        int fatal = 0,
        int error = 0,
        int warning = 0,
        IReadOnlyList<CheckMessage>? messages = null)
        => new(fatal, error, warning, 0, messages ?? Array.Empty<CheckMessage>());

    private static CheckMessage Error(string id, string text, string path, int line, int column)
        => new(CheckSeverity.Error, id, text, new[] { new CheckLocation(path, line, column) });
}
