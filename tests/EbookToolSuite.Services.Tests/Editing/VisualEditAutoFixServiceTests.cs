using System.Text;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Services.Editing;

namespace EbookToolSuite.Services.Tests.Editing;

/// <summary>
/// 可视编辑"自动修正"链路（T5-2W 第 5 步 / 判据 12）。真实文件系统 + 真实还原点服务 + 真实校验器，
/// 只有"修正后仍不合格"和"备份建不起来"这两种边角用替身制造。
///
/// 判据 12 的三件事：修正前要有还原点（1）、修正后要写 operation_log(autofix)（2）、
/// 修正失败要整体回滚（原文零改动、不留垃圾还原点）（3）。
/// </summary>
public sealed class VisualEditAutoFixServiceTests : IDisposable
{
    private const string Entry = "OEBPS/Text/ch1.xhtml";

    private static readonly Guid BookId = Guid.NewGuid();

    private static readonly Guid FormatId = Guid.NewGuid();

    private static readonly SourceFix SampleFix =
        new("void-tag", 3, "<br>", "<br/>", "空标签没自闭合");

    /// <summary>一份正常的章节原文（Assemble 会换掉 body 里的内容，head 原样保留）。</summary>
    private const string OriginalChapter =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<html xmlns=\"http://www.w3.org/1999/xhtml\">\n"
        + "<head><title>冒烟章</title><link rel=\"stylesheet\" href=\"../Styles/t.css\"/></head>\n"
        + "<body><p>原始正文</p></body>\n"
        + "</html>\n";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly FakeOperationLog _operations = new();
    private readonly RestorePointService _restorePoints;
    private readonly ChapterSourceService _source = new();
    private readonly VisualEditAutoFixService _service;
    private readonly FakeWorkspace _workspace;

    public VisualEditAutoFixServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ets-autofix-" + Guid.NewGuid().ToString("N"));
        var appRoot = Path.Combine(_root, "app");
        var userLocal = Path.Combine(_root, "localappdata", "EbookToolSuite");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(userLocal);

        _paths = AppPaths.Create(new AppPathsOptions
        {
            AppRoot = appRoot,
            UserLocalRoot = userLocal,
            Fallback = DataDirFallback.Throw,
        });
        _paths.EnsureCreated();

        _restorePoints = new RestorePointService(_paths, _operations);
        _service = new VisualEditAutoFixService(_source, _restorePoints, _operations);
        _workspace = new FakeWorkspace(Path.Combine(_root, "work"));

        WriteChapter(OriginalChapter);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响结论
        }
    }

    // ---------- 正常路径：先备份、再改盘、后记账 ----------

    [Fact]
    public async Task 有可修正项_先建还原点再落盘_并写operation_log()
    {
        const string Body = "<div><p>甲<br>乙&nbsp;丙</p>";

        // 前置：这一段确实"能修但还没改"
        var scan = _source.SaveVisual(_workspace, Entry, Body, applyFixes: false);
        Assert.False(scan.Ok);
        Assert.True(scan.NeedsFix);
        Assert.NotEmpty(scan.Fixes);

        var beforeFix = File.ReadAllText(ChapterPath);

        var outcome = await _service.ApplyAsync(_workspace, Entry, Body);

        // 1) 修正落盘了，而且是用修正后的文本
        var afterFix = File.ReadAllText(ChapterPath);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.True(outcome.Applied);
        Assert.NotEqual(beforeFix, afterFix);
        Assert.Contains("甲", afterFix, StringComparison.Ordinal);
        Assert.Contains("&#160;", afterFix, StringComparison.Ordinal);
        Assert.Contains("<br/>", afterFix, StringComparison.Ordinal);
        Assert.Contains(Entry, _workspace.Session.ChangedFiles);

        // 2) 修正前建了还原点，里面备份的正是"修正前那一份原文"
        Assert.NotNull(outcome.RestorePointId);

        var point = await _restorePoints.GetAsync(outcome.RestorePointId!);

        Assert.NotNull(point);
        Assert.Equal(RestorePointTypes.AutoFix, point!.Type);
        Assert.Equal(1, point.ItemCount);
        Assert.Contains(Entry, point.Note!, StringComparison.Ordinal);

        var item = point.Items[0];

        Assert.Equal(BookId, item.BookId);
        Assert.Equal(FormatId, item.FormatId);
        Assert.Equal(ChapterPath, item.OriginalPath, ignoreCase: true);
        Assert.Equal(
            beforeFix,
            await File.ReadAllTextAsync(Path.Combine(_restorePoints.PointDirectory(point.Id), item.BackupFile)));

        // 3) operation_log 里记了一笔 autofix，指向那个还原点
        var log = Assert.Single(_operations.Entries);
        Assert.Equal(OperationNames.AutoFix, log.Operation);
        Assert.Equal(BookId, log.BookId);
        Assert.Equal(FormatId, log.FormatId);
        Assert.Equal(point.Id, log.RestorePointId);
        Assert.Contains(Entry, log.Summary, StringComparison.Ordinal);
        Assert.Contains(Entry, log.Detail!, StringComparison.Ordinal);
    }

    // ---------- 不需要修正：不建还原点、不写日志（内容由 SaveVisual 自己写进去） ----------

    [Fact]
    public async Task 不需要修正时_不建还原点_不写日志()
    {
        // 顺带钉住冒烟 V8 用的那段正文确实是"干净"的（不然冒烟会弹出真弹窗把自动化卡住）
        var scan = _source.SaveVisual(_workspace, Entry, "<p>可视保存冒烟</p>", applyFixes: false);

        Assert.True(scan.Ok, scan.Check.Summary);

        var outcome = await _service.ApplyAsync(_workspace, Entry, "<p>可视保存冒烟</p>");

        Assert.True(outcome.Ok);
        Assert.False(outcome.Applied);
        Assert.Null(outcome.RestorePointId);
        Assert.Empty(await _restorePoints.ListAsync());
        Assert.Empty(_operations.Entries);
        Assert.Contains("可视保存冒烟", File.ReadAllText(ChapterPath), StringComparison.Ordinal);
    }

    // ---------- 判据 12 的"失败整体回滚" ----------

    [Fact]
    public async Task 修正后仍不合格_一个字节都不写_还原点也不留()
    {
        var chapter = OriginalChapter;
        WriteChapter(chapter);

        var fake = new FakeSource((_, _, _, applyFixes) => applyFixes
            ? new ChapterVisualSaveResult(
                false, true, new[] { SampleFix }, ChapterSourceCheck.Success, "自动修正后仍不合格：第 3 行")
            : new ChapterVisualSaveResult(
                false, true, new[] { SampleFix }, ChapterSourceCheck.Success, "有 1 处需要自动修正，等确认"));

        var service = new VisualEditAutoFixService(fake, _restorePoints, _operations);

        var outcome = await service.ApplyAsync(_workspace, Entry, "<p>x</p>");

        Assert.False(outcome.Ok);
        Assert.True(outcome.Applied);
        Assert.Null(outcome.RestorePointId);
        Assert.Contains("仍不合格", outcome.Message, StringComparison.Ordinal);

        // 原文零改动：文件内容跟修正前一样
        Assert.Equal(chapter, File.ReadAllText(ChapterPath));

        // 没用了的还原点要清掉，也不要记账
        Assert.Empty(await _restorePoints.ListAsync());
        Assert.Empty(_operations.Entries);

        // 两次调用：先扫、后应用（应用返回失败）
        Assert.Equal(new[] { false, true }, fake.ApplyFlags);
    }

    [Fact]
    public async Task 修正前的备份建不起来_改成拦住保存_不往下走()
    {
        WriteChapter(OriginalChapter);

        // 替身在"扫描"这一步把章节文件删掉，制造"要备份时文件已经不在了"
        var fake = new FakeSource((workspace, entry, _, applyFixes) =>
        {
            if (!applyFixes)
            {
                File.Delete(Path.Combine(workspace.WorkDir, entry.Replace('/', Path.DirectorySeparatorChar)));
            }

            return new ChapterVisualSaveResult(
                false, true, new[] { SampleFix }, ChapterSourceCheck.Success, "有 1 处需要自动修正，等确认");
        });

        var service = new VisualEditAutoFixService(fake, _restorePoints, _operations);

        var outcome = await service.ApplyAsync(_workspace, Entry, "<p>x</p>");

        Assert.False(outcome.Ok);
        Assert.False(outcome.Applied);
        Assert.Null(outcome.RestorePointId);
        Assert.Contains("备份失败", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(await _restorePoints.ListAsync());
        Assert.Empty(_operations.Entries);

        // 只调了"扫描"那一次：备份都没成功，绝不能再往下改盘
        Assert.Equal(new[] { false }, fake.ApplyFlags);
    }

    // ---------- 辅助 ----------

    private string ChapterPath
        => Path.Combine(_workspace.WorkDir, Entry.Replace('/', Path.DirectorySeparatorChar));

    private void WriteChapter(string source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ChapterPath)!);
        File.WriteAllText(ChapterPath, source, new UTF8Encoding(false));
    }

    private sealed class FakeWorkspace : IEpubWorkspace
    {
        public FakeWorkspace(string workDir)
        {
            WorkDir = workDir;
            Directory.CreateDirectory(workDir);
            Session = new WorkspaceSession
            {
                SessionId = "t5b2w",
                BookId = BookId,
                FormatId = FormatId,
                SourcePath = Path.Combine(workDir, "原书.epub"),
            };
        }

        public string SessionId => Session.SessionId;

        public string WorkDir { get; }

        public WorkspaceSession Session { get; }

        public Task DiscardAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSource : IChapterSourceService
    {
        private readonly Func<IEpubWorkspace, string, string, bool, ChapterVisualSaveResult> _onSaveVisual;

        public FakeSource(Func<IEpubWorkspace, string, string, bool, ChapterVisualSaveResult> onSaveVisual)
            => _onSaveVisual = onSaveVisual;

        /// <summary>每次 SaveVisual 的 applyFixes 传了什么（用来证明"备份失败就不往下走"）。</summary>
        public List<bool> ApplyFlags { get; } = new();

        public string ReadSource(IEpubWorkspace workspace, string entry) => string.Empty;

        public ChapterSourceCheck Check(string source) => ChapterSourceCheck.Success;

        public ChapterSourceCheck Save(IEpubWorkspace workspace, string entry, string source)
            => ChapterSourceCheck.Success;

        public ChapterVisualSaveResult SaveVisual(
            IEpubWorkspace workspace, string entry, string bodyInnerXml, bool applyFixes = false)
        {
            ApplyFlags.Add(applyFixes);
            return _onSaveVisual(workspace, entry, bodyInnerXml, applyFixes);
        }
    }

    /// <summary>收 operation_log 的替身（真库那套由仓储测试覆盖）。</summary>
    private sealed class FakeOperationLog : IOperationLogRepository
    {
        public List<OperationLogEntry> Entries { get; } = new();

        public Task<long> AddAsync(OperationLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.FromResult((long)Entries.Count);
        }

        public Task<IReadOnlyList<OperationLogEntry>> ListRecentAsync(int take = 100, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<OperationLogEntry>)Entries.Take(take).ToList());

        public Task<IReadOnlyList<OperationLogEntry>> ListForBookAsync(
            Guid bookId, int take = 50, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<OperationLogEntry>)Entries
                .Where(e => e.BookId == bookId)
                .Take(take)
                .ToList());

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(Entries.Count);
    }
}