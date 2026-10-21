using System.Linq;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Services.Editing;

/// <summary>
/// 章节源码读写（执行计划 T5-2）。校验逻辑在引擎里（<see cref="ChapterSourceEditor"/>），
/// 这里负责"通过才落盘 + 记账"。
/// </summary>
public sealed class ChapterSourceService : IChapterSourceService
{
    private readonly ChapterSourceEditor _editor = new();
    private readonly ChapterSourceFixer _fixer = new();

    public string ReadSource(IEpubWorkspace workspace, string entry)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return _editor.ReadSource(workspace.WorkDir, entry);
    }

    public ChapterSourceCheck Check(string source) => _editor.Check(source);

    public ChapterSourceCheck Save(IEpubWorkspace workspace, string entry, string source)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);

        var check = _editor.Check(source);

        if (!check.Ok)
        {
            // 判据 3 / 4：语法不过就不写盘，把行号与人话提示原样交回界面
            return check;
        }

        _editor.WriteSource(workspace.WorkDir, entry, source);
        RecordChange(workspace, entry);

        return check;
    }

    public ChapterVisualSaveResult SaveVisual(
        IEpubWorkspace workspace, string entry, string bodyInnerXml, bool applyFixes = false)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);

        var original = _editor.ReadSource(workspace.WorkDir, entry);
        var assembled = ChapterVisualEdit.Assemble(original, bodyInnerXml ?? string.Empty);
        var check = _editor.Check(assembled);

        if (check.Ok)
        {
            Write(workspace, entry, assembled);
            return new ChapterVisualSaveResult(true, false, Array.Empty<SourceFix>(), check, "已写入工作区");
        }

        var (fixedText, fixes) = _fixer.Fix(assembled);

        if (fixes.Count == 0)
        {
            return new ChapterVisualSaveResult(false, false, Array.Empty<SourceFix>(), check, "保存被拦：" + check.Summary);
        }

        if (!applyFixes)
        {
            var count = fixes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new ChapterVisualSaveResult(false, true, fixes, check, "有 " + count + " 处需要自动修正，等确认");
        }

        var recheck = _editor.Check(fixedText);

        if (!recheck.Ok)
        {
            return new ChapterVisualSaveResult(false, true, fixes, recheck, "自动修正后仍不合格：" + recheck.Summary);
        }

        Write(workspace, entry, fixedText);

        var applied = fixes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ChapterVisualSaveResult(true, true, fixes, recheck, "已自动修正 " + applied + " 处并写入工作区");
    }

    private void Write(IEpubWorkspace workspace, string entry, string source)
    {
        _editor.WriteSource(workspace.WorkDir, entry, source);
        RecordChange(workspace, entry);
    }

    /// <summary>把改过的条目记进 .session.json 的 changedFiles（同一个文件不重复记）。</summary>
    private static void RecordChange(IEpubWorkspace workspace, string entry)
    {
        var session = workspace.Session;

        if (!session.ChangedFiles.Contains(entry, StringComparer.OrdinalIgnoreCase))
        {
            session.ChangedFiles.Add(entry);
        }

        EpubWorkspaceFactory.WriteSession(workspace.WorkDir, session);
    }
}
