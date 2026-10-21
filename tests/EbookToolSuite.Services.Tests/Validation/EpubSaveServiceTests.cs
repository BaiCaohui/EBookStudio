using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Services.Editing;
using EbookToolSuite.Services.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Validation;

/// <summary>
/// 两段式保存（T3-5）：真实文件系统、真实打包器、真实工作区（EpubWorkspaceFactory 解包），
/// 只有"校验器"是替身因为真 EpubCheck 要跑 7 秒且要 java，那是集成测试的事。
///
/// 钉死的核心事实：
///   1. Stage 阶段**绝不能碰原文件**（校验跑的是暂存文件）；
///   2. 有 error 时 Stage 给出 Blocked 且原文件字节不变（这就是"阻止覆盖"）；
///   3. 用户选「仍然保存」后 Commit 能成功（计划的哲学：永远给用户最终决定权）；
///   4. Discard 之后原文件与暂存文件都不留残渣。
/// </summary>
public sealed class EpubSaveServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly EpubWorkspaceFactory _workspaces;
    private readonly EpubPackager _packager = new();
    private readonly string _target;
    private readonly byte[] _originalBytes;

    public EpubSaveServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ets-save-" + Guid.NewGuid().ToString("N"));
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

        _workspaces = new EpubWorkspaceFactory(_paths, SystemClock.Instance, NullLogger<EpubWorkspaceFactory>.Instance);

        _target = new EpubBuilder { Title = "保存测试书", ChapterCount = 2, CoverBytes = EpubBuilder.MakeJpeg() }
            .WriteTo(Path.Combine(_root, "book.epub"));
        _originalBytes = File.ReadAllBytes(_target);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 清不掉不影响结论
        }
    }

    // ---------- 判据 1：校验通过 -> 静默替换 ----------

    [Fact]
    public async Task 校验通过_Stage不碰原文件_Commit才替换()
    {
        var checker = new FakeEpubChecker(Report());
        var save = NewSave(checker);

        await using var workspace = await OpenWorkspace();

        var stage = await save.StageAsync(workspace.WorkDir, _target);

        // 暂存文件躺在目标旁边，原文件一个字节都没动
        Assert.Equal(Path.GetFullPath(_target) + EpubSaveService.StagedSuffix, stage.StagedPath);

        // 回归：暂存名必须以 .epub 结尾。EpubCheck 靠扩展名认输入格式，
        // 实测 .epub.ets-stage 会让它退出码 1 且**不写报告**（静默失败），保存会被门禁莫名拦下。
        Assert.Equal(".epub", Path.GetExtension(stage.StagedPath));
        Assert.True(File.Exists(stage.StagedPath));
        Assert.Equal(_originalBytes, File.ReadAllBytes(_target));
        Assert.Equal(ValidationOutcome.Pass, stage.Decision.Outcome);

        // 校验跑的是**暂存文件**，绝不是原文件
        Assert.Equal(stage.StagedPath, Assert.Single(checker.SeenPaths));

        var result = await save.CommitAsync(stage);

        Assert.False(File.Exists(stage.StagedPath));
        Assert.Equal(_target, result.OutputPath);
        Assert.NotEqual(_originalBytes, File.ReadAllBytes(_target));

        // 替换后的文件必须是结构合法的 EPUB（mimetype 第一且 STORED 等）
        var verification = EpubPackageVerifier.Verify(_target);
        Assert.True(verification.EntryCount > 0);
        Assert.Equal(_target, stage.Package.OutputPath.Replace(EpubSaveService.StagedSuffix, string.Empty));
    }

    // ---------- 判据 2：有 error -> 阻止覆盖；仍然保存 -> 能过 ----------

    [Fact]
    public async Task 有错误_阻止覆盖且原文件零改动()
    {
        var checker = new FakeEpubChecker(Report(error: 1, messages: new[]
        {
            new CheckMessage(CheckSeverity.Error, "OPF-003", "manifest 里有未声明的文件",
                new[] { new CheckLocation("OEBPS/content.opf", 3, 1) }),
        }));
        var save = NewSave(checker);

        await using var workspace = await OpenWorkspace();

        var stage = await save.StageAsync(workspace.WorkDir, _target);

        Assert.Equal(ValidationOutcome.Blocked, stage.Decision.Outcome);
        Assert.True(stage.Decision.NeedsConfirmation);
        Assert.Equal("仍然保存（不推荐）", stage.Decision.ConfirmActionText);

        // 关键：原文件零改动，暂存产物还在（用户随时可以改主意再提交）
        Assert.Equal(_originalBytes, File.ReadAllBytes(_target));
        Assert.True(File.Exists(stage.StagedPath));
        Assert.Contains("OPF-003", stage.Decision.TopMessages()[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 有错误但用户仍然保存_Commit成功()
    {
        var checker = new FakeEpubChecker(Report(error: 3));
        var save = NewSave(checker);

        await using var workspace = await OpenWorkspace();

        var stage = await save.StageAsync(workspace.WorkDir, _target);
        Assert.Equal(ValidationOutcome.Blocked, stage.Decision.Outcome);

        // 「仍然保存（不推荐）」走的还是同一个 Commit
        await save.CommitAsync(stage);

        Assert.NotEqual(_originalBytes, File.ReadAllBytes(_target));
        Assert.False(File.Exists(stage.StagedPath));
        EpubPackageVerifier.Verify(_target);
    }

    [Fact]
    public async Task 取消_丢掉暂存产物_原文件与工作区都还在()
    {
        var checker = new FakeEpubChecker(Report(error: 1));
        var save = NewSave(checker);

        await using var workspace = await OpenWorkspace();

        var stage = await save.StageAsync(workspace.WorkDir, _target);
        save.Discard(stage);

        Assert.False(File.Exists(stage.StagedPath));
        Assert.Equal(_originalBytes, File.ReadAllBytes(_target));
        Assert.True(Directory.Exists(workspace.WorkDir));
    }

    // ---------- 判据 3：只有警告 -> 放行 ----------

    [Fact]
    public async Task 只有警告_直接放行且摘要提警告数()
    {
        var checker = new FakeEpubChecker(Report(warning: 2));
        var save = NewSave(checker);

        await using var workspace = await OpenWorkspace();

        var stage = await save.StageAsync(workspace.WorkDir, _target);

        Assert.Equal(ValidationOutcome.PassWithWarnings, stage.Decision.Outcome);
        Assert.True(stage.Decision.IsSilentPass);
        Assert.Contains("2", stage.Decision.Summary, StringComparison.Ordinal);

        await save.CommitAsync(stage);
        Assert.NotEqual(_originalBytes, File.ReadAllBytes(_target));
    }

    // ---------- 判据 5：超时 ----------

    [Fact]
    public async Task 校验超时_给出跳过校验的选项_且仍可提交()
    {
        var checker = new FakeEpubChecker(_ => throw new AppException(
            ErrorCode.ProcessTimeout, "EpubCheck 超过 60 秒还没跑完，已终止。"));
        var save = NewSave(checker);

        await using var workspace = await OpenWorkspace();

        var stage = await save.StageAsync(workspace.WorkDir, _target);

        Assert.Equal(ValidationOutcome.TimedOut, stage.Decision.Outcome);
        Assert.True(stage.Decision.NeedsConfirmation);
        Assert.Equal("跳过校验保存", stage.Decision.ConfirmActionText);
        Assert.Equal(_originalBytes, File.ReadAllBytes(_target));

        await save.CommitAsync(stage);
        Assert.NotEqual(_originalBytes, File.ReadAllBytes(_target));
    }

    // ---------- 判据 6：没装工具也要能保存 ----------

    [Fact]
    public async Task 工具缺失_跳过校验直接放行()
    {
        var checker = new FakeEpubChecker(_ => throw new AppException(
            ErrorCode.MissingTool, "没找到 EpubCheck（Tools/EpubCheck/epubcheck.jar），无法校验。"));
        var save = NewSave(checker);

        await using var workspace = await OpenWorkspace();

        var stage = await save.StageAsync(workspace.WorkDir, _target);

        Assert.Equal(ValidationOutcome.ToolMissing, stage.Decision.Outcome);
        Assert.True(stage.Decision.IsSilentPass);
        Assert.False(stage.Decision.NeedsConfirmation);

        await save.CommitAsync(stage);
        Assert.NotEqual(_originalBytes, File.ReadAllBytes(_target));
    }

    [Fact]
    public async Task 校验跑挂_不硬拦但也不当通过()
    {
        var checker = new FakeEpubChecker(_ => throw new AppException(ErrorCode.ProcessCrashed, "EpubCheck 运行失败。"));
        var save = NewSave(checker);

        await using var workspace = await OpenWorkspace();

        var stage = await save.StageAsync(workspace.WorkDir, _target);

        Assert.Equal(ValidationOutcome.ToolFailed, stage.Decision.Outcome);
        Assert.True(stage.Decision.NeedsConfirmation);
        Assert.Equal(_originalBytes, File.ReadAllBytes(_target));
    }

    // ---------- T3-6：保存前先备份 + 失败回滚 ----------

    [Fact]
    public async Task 提交前自动建还原点_备份内容等于替换前的内容()
    {
        var checker = new FakeEpubChecker(Report());
        var restorePoints = new RestorePointService(_paths);
        var save = new EpubSaveService(_packager, checker, restorePoints);

        await using var workspace = await OpenWorkspace();

        var stage = await save.StageAsync(workspace.WorkDir, _target);
        var bookId = Guid.NewGuid();
        var formatId = Guid.NewGuid();

        var result = await save.CommitAsync(
            stage,
            new RestorePointRequest(bookId, formatId, _target));

        // 判据 1：Backups 里有对应还原点
        Assert.False(string.IsNullOrWhiteSpace(result.RestorePointId));
        var point = await restorePoints.GetAsync(result.RestorePointId!);
        Assert.NotNull(point);
        Assert.Equal(1, point!.ItemCount);
        Assert.Equal(bookId, point.Items[0].BookId);
        Assert.Equal(formatId, point.Items[0].FormatId);
        Assert.Equal(_target, point.Items[0].OriginalPath);

        // 备份里存的必须是**替换前**的内容：逐字节等于原始的 _originalBytes
        var backupPath = Path.Combine(restorePoints.PointDirectory(point.Id), point.Items[0].BackupFile);
        Assert.Equal(_originalBytes, File.ReadAllBytes(backupPath));
        Assert.Equal(point.Items[0].OriginalSize, _originalBytes.Length);
        Assert.Equal(await RestorePointService.HashAsync(backupPath), point.Items[0].OriginalHash);

        // 而目标文件已经被换成新内容了
        Assert.NotEqual(_originalBytes, File.ReadAllBytes(_target));
    }

    [Fact]
    public async Task 被独占占用_备份这一步就失败_原文件原样且不留还原点()
    {
        var checker = new FakeEpubChecker(Report());
        var restorePoints = new RestorePointService(_paths);
        var save = new EpubSaveService(_packager, checker, restorePoints);

        await using var workspace = await OpenWorkspace();

        var stage = await save.StageAsync(workspace.WorkDir, _target);

        // 独占占住：连读都读不到，先失败的其实是"备份"这一步。
        // 注意：占着的时候连测试自己都读不了文件，所以断言要等释放句柄之后再做。
        ErrorCode code;
        await using (var hold = new FileStream(_target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<AppException>(
                () => save.CommitAsync(stage, new RestorePointRequest(Guid.NewGuid(), Guid.NewGuid(), _target)));

            code = ex.Code;
            Assert.Empty(await restorePoints.ListAsync());
        }

        Assert.Equal(ErrorCode.FileLocked, code);

        // 没备成就绝不替换：原文件原样，也不该留下半成品还原点
        Assert.Equal(_originalBytes, File.ReadAllBytes(_target));
        Assert.True(File.Exists(stage.StagedPath));
    }

    [Fact]
    public async Task 只允许读_备份成功但替换失败_原文件原样且还原点在()
    {
        var checker = new FakeEpubChecker(Report());
        var restorePoints = new RestorePointService(_paths);
        var save = new EpubSaveService(_packager, checker, restorePoints);

        await using var workspace = await OpenWorkspace();

        var stage = await save.StageAsync(workspace.WorkDir, _target);

        // 只允许别人读：File.Copy（备份）能成，File.Replace 要独占写 -> 失败。
        // 这正是"备份成功但替换失败"的真实现场（阅读器开着这本书）。
        await using var hold = new FileStream(_target, FileMode.Open, FileAccess.Read, FileShare.Read);

        var ex = await Assert.ThrowsAsync<AppException>(
            () => save.CommitAsync(stage, new RestorePointRequest(Guid.NewGuid(), Guid.NewGuid(), _target)));

        Assert.Equal(ErrorCode.FileLocked, ex.Code);

        // 判据 5/6：替换失败后原文件必须还是原样，且还原点留着（随时能回退）
        Assert.Equal(_originalBytes, File.ReadAllBytes(_target));
        Assert.Single(await restorePoints.ListAsync());
        Assert.True(File.Exists(stage.StagedPath));
    }

    [Fact]
    public async Task 目标文件还不存在时_不建还原点()
    {
        var checker = new FakeEpubChecker(Report());
        var restorePoints = new RestorePointService(_paths);
        var save = new EpubSaveService(_packager, checker, restorePoints);

        await using var workspace = await OpenWorkspace();
        var missing = Path.Combine(_root, "新书.epub");

        var stage = await save.StageAsync(workspace.WorkDir, missing);
        var result = await save.CommitAsync(
            stage,
            new RestorePointRequest(Guid.NewGuid(), Guid.NewGuid(), missing));

        // 没有"原文件"可备份，就不该留下空还原点
        Assert.Null(result.RestorePointId);
        Assert.Empty(await restorePoints.ListAsync());
        Assert.True(File.Exists(missing));
    }

    // ---------- 边界 ----------

    [Fact]
    public async Task 目标文件不存在时_Commit用Move也能落地()
    {
        var missing = Path.Combine(_root, "新书.epub");
        var checker = new FakeEpubChecker(Report());
        var save = NewSave(checker);

        await using var workspace = await OpenWorkspace();

        var stage = await save.StageAsync(workspace.WorkDir, missing);
        await save.CommitAsync(stage);

        Assert.True(File.Exists(missing));
        EpubPackageVerifier.Verify(missing);
    }

    [Fact]
    public async Task 暂存残渣会被下一次Stage清掉()
    {
        var checker = new FakeEpubChecker(Report());
        var save = NewSave(checker);

        await using var workspace = await OpenWorkspace();

        var leftover = Path.GetFullPath(_target) + EpubSaveService.StagedSuffix;
        await File.WriteAllTextAsync(leftover, "上一轮的残渣", new UTF8Encoding(false));

        var stage = await save.StageAsync(workspace.WorkDir, _target);

        // 旧残渣必须先被清掉，否则提交时会被占用挡住
        Assert.True(File.Exists(stage.StagedPath));
        Assert.Equal(ValidationOutcome.Pass, stage.Decision.Outcome);
        save.Discard(stage);
    }

    [Fact]
    public async Task 暂存产物不见了_提交时报FileNotFound而不是悄悄成功()
    {
        var checker = new FakeEpubChecker(Report());
        var save = NewSave(checker);

        await using var workspace = await OpenWorkspace();

        var stage = await save.StageAsync(workspace.WorkDir, _target);
        File.Delete(stage.StagedPath);

        var ex = await Assert.ThrowsAsync<AppException>(() => save.CommitAsync(stage));

        Assert.Equal(ErrorCode.FileNotFound, ex.Code);
        Assert.Equal(_originalBytes, File.ReadAllBytes(_target));
    }

    // ---------- 辅助 ----------

    private EpubSaveService NewSave(IEpubChecker checker) => new(_packager, checker, log: null);

    private async Task<IEpubWorkspace> OpenWorkspace()
        => await _workspaces.OpenAsync(new WorkspaceOpenRequest { EpubPath = _target });

    private static CheckReport Report(
        int fatal = 0,
        int error = 0,
        int warning = 0,
        IReadOnlyList<CheckMessage>? messages = null)
        => new(fatal, error, warning, 0, messages ?? Array.Empty<CheckMessage>());

    /// <summary>校验器替身：可预设报告，也可直接抛（模拟超时 / 工具缺失）。</summary>
    private sealed class FakeEpubChecker : IEpubChecker
    {
        private readonly Func<string, CheckReport> _responder;

        public FakeEpubChecker(CheckReport report) : this(_ => report)
        {
        }

        public FakeEpubChecker(Func<string, CheckReport> responder) => _responder = responder;

        public int CallCount { get; private set; }

        public List<string> SeenPaths { get; } = new();

        public Task<CheckReport> ValidateAsync(string epubPath, CancellationToken ct = default)
        {
            CallCount++;
            SeenPaths.Add(epubPath);
            return Task.FromResult(_responder(epubPath));
        }
    }
}
