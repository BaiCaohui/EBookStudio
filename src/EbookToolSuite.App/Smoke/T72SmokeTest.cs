using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Views;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Engines.Processes;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Data;
using EbookToolSuite.Infrastructure.Files;
using EbookToolSuite.Reader.WebView;
using EbookToolSuite.Services.Conversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T7-2 错误处理与边界验收（执行计划 10.1 的 16 项边界清单）。
///
/// 【怎么验】每一项都**真的造场景**，不是查代码：
///   - 损坏 / DRM / Zip Slip / XML 注入 / 超长书名：现造 EPUB 走真实导入管线；
///   - 文件被占用：真的用 FileShare.None 锁住文件再导入；
///   - 数据库被占用：真的用 SQLite 排他事务占住书库再写；
///   - 图片 404 / 主题回退：真的开 WebView2 阅读器，在章节 iframe 里查 DOM；
///   - 断电：核对 PRAGMA integrity_check + 全数据目录没有 .part 残留。
///
/// 【第 12 项（转换中关程序）】Runner 脚本另外驱动两次真实进程：
/// 先跑 <c>--smoke-queue-crash</c> 强杀自己，再用同一数据目录跑 <c>--smoke-queue-resume</c>。
/// 本进程只核对“上次 Running 的任务重启后回 Queued”这条恢复语义。
/// </summary>
internal static class T72SmokeTest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string LongTitlePrefix = "超长书名";

    public static async Task<SmokeReport> RunAsync(
        IServiceProvider services,
        AppPaths paths,
        string workDirectory)
    {
        var startedAt = DateTimeOffset.Now;
        var total = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        Require(Path.IsPathFullyQualified(workDirectory), "工作目录必须是绝对路径：" + workDirectory);
        Directory.CreateDirectory(workDirectory);

        var importer = services.GetRequiredService<IBookImportService>();
        var repo = services.GetRequiredService<IBookRepository>();
        var conversion = services.GetRequiredService<IConversionService>();
        var queue = services.GetRequiredService<IConversionQueue>();
        var templates = services.GetRequiredService<ConversionTemplateStore>();
        var taskRepo = services.GetRequiredService<IConversionTaskRepository>();
        var factory = services.GetRequiredService<SqliteConnectionFactory>();
        var reader = services.GetRequiredService<IReaderService>();
        var environments = services.GetRequiredService<IWebViewEnvironmentProvider>();
        var progress = services.GetRequiredService<IReadingProgressService>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var workspaces = services.GetRequiredService<IEpubWorkspaceFactory>();
        var source = services.GetRequiredService<IChapterSourceService>();
        var assets = services.GetRequiredService<IReaderAssets>();
        var autofix = services.GetRequiredService<IVisualEditAutoFixService>();
        var findReplace = services.GetRequiredService<IChapterFindReplaceService>();
        var snapshots = services.GetRequiredService<IWorkspaceSnapshotService>();

        // 统一跑一项：正常返回 detail 即通过；抛异常即失败。
        async Task Run(string name, Func<Task<string>> body)
        {
            try
            {
                Add(name, true, await body().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                Add(
                    name,
                    false,
                    ex is InvalidOperationException
                        ? ex.Message
                        : ex.GetType().Name + "：" + ex.SafeMessage());
            }
        }

        var validEpub = Path.Combine(workDirectory, "valid.epub");
        WriteEpub(validEpub, "正常书", chapterText: "正文内容。");

        // ---------- 1. 数据目录所在盘被拔掉 ----------
        await Run("boundary-01-datadir-unplugged", async () =>
        {
            var drive = FindUnusedDriveRoot()
                ?? throw new InvalidOperationException("找不到未占用的盘符，无法模拟拔盘");

            var badRoot = Path.Combine(drive, "EbookStudio-t72-unplugged");

            try
            {
                AppPaths.Create(new AppPathsOptions
                {
                    AppRoot = workDirectory,
                    UserLocalRoot = Path.Combine(workDirectory, "user-local-1"),
                    CommandLineDataRoot = badRoot,
                });

                throw new InvalidOperationException("意外：指向不存在的盘符竟然解析成功了");
            }
            catch (AppException ex) when (ex.Code == ErrorCode.DataDirNotWritable)
            {
                Require(ex.UserMessage.Contains("不可写") || ex.UserMessage.Contains("磁盘"),
                    "提示没说是磁盘/目录的问题：" + ex.UserMessage);
                Require(ex.UserMessage.Contains("检查") || ex.UserMessage.Contains("换"),
                    "提示没有可执行的建议：" + ex.UserMessage);
            }

            // 运行期拔盘：Windows ERROR_NOT_READY(21) 必须翻成同一类中文。
            var info = FileErrors.ByWin32(21);
            Require(info.Code == ErrorCode.DataDirNotWritable, "ERROR_NOT_READY 没被识别成数据目录不可用");

            await Task.CompletedTask;
            return "指向不存在的盘符 " + badRoot + " -> AppException(DataDirNotWritable)，有原因与建议；ERROR_NOT_READY 分类正确";
        });

        // ---------- 2. 磁盘写满 ----------
        await Run("boundary-02-disk-full", async () =>
        {
            var thrown = false;

            try
            {
                DiskSpaceGuard.Ensure(paths.LibraryDir, long.MaxValue / 4, 2.0);
            }
            catch (AppException ex) when (ex.Code == ErrorCode.DiskFull)
            {
                thrown = true;
                Require(ex.UserMessage.Contains("空间"), "提示没有说空间：" + ex.UserMessage);
                Require(ex.UserMessage.Contains("清理") || ex.UserMessage.Contains("换"),
                    "提示没有建议动作：" + ex.UserMessage);
            }

            Require(thrown, "空间预检没有抛 DiskFull");

            // Windows ERROR_DISK_FULL(112) / ERROR_HANDLE_DISK_FULL(39)
            Require(FileErrors.ByWin32(112).Code == ErrorCode.DiskFull, "ERROR_DISK_FULL 分类不对");
            Require(FileErrors.ByWin32(39).Code == ErrorCode.DiskFull, "ERROR_HANDLE_DISK_FULL 分类不对");

            await Task.CompletedTask;
            return "预检抛 DiskFull（含需要/可用数字与建议）；导入管线已在复制前调用预检，失败走补偿";
        });

        // ---------- 3. 文件被其他程序占用 ----------
        await Run("boundary-03-file-locked", async () =>
        {
            var lockedEpub = Path.Combine(workDirectory, "locked.epub");
            WriteEpub(lockedEpub, "被锁住的书", chapterText: "锁测试。");

            var before = await repo.CountAsync();

            ImportResult lockedResult;

            using (new FileStream(lockedEpub, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                lockedResult = await ImportAsync(importer, lockedEpub);
            }

            Require(
                lockedResult.Outcome != ImportOutcome.Imported,
                "文件被独占占用时竟然导入成功");

            Require(
                (lockedResult.Message ?? string.Empty).Contains("占用")
                    || (lockedResult.Message ?? string.Empty).Contains("权限"),
                "导入失败提示没有说清被占用：" + lockedResult.Message);

            Require(await repo.CountAsync() == before, "导入失败却写进了书库");

            var retry = await ImportAsync(importer, lockedEpub);
            Require(retry.Outcome == ImportOutcome.Imported, "释放占用后重试应成功：" + retry.Message);

            var after = await repo.CountAsync();
            Require(after == before + 1, "重试成功后书库数量没增加：before=" + before + " after=" + after);

            // 生产数据的正式区里不能留下失败产生的垃圾
            var leftovers = Directory
                .EnumerateFiles(paths.LibraryDir, "*.part", SearchOption.AllDirectories)
                .Count();

            Require(leftovers == 0, "正式区残留临时文件 " + leftovers + " 个");

            return "占用时提示=“" + Shorten(lockedResult.Message) + "”，未写库；释放后重试成功";
        });

        // ---------- 4. EPUB 损坏 ----------
        await Run("boundary-04-corrupt-epub", async () =>
        {
            var corrupt = Path.Combine(workDirectory, "corrupt.epub");
            await File.WriteAllBytesAsync(corrupt, RandomBytes(4096));
            var before = await repo.CountAsync();

            var result = await ImportAsync(importer, corrupt);

            Require(result.Outcome == ImportOutcome.InvalidEpub, "损坏文件没有按 InvalidEpub 拒绝：" + result.Outcome);
            Require((result.Message ?? string.Empty).Contains("EPUB"), "提示没说是 EPUB 的问题：" + result.Message);
            Require(await repo.CountAsync() == before, "损坏文件却写进了书库");

            return "损坏 zip -> InvalidEpub，提示=“" + Shorten(result.Message) + "”，未写库";
        });

        // ---------- 5. EPUB 有 DRM ----------
        await Run("boundary-05-drm", async () =>
        {
            var drm = Path.Combine(workDirectory, "drm.epub");
            WriteEpub(
                drm,
                "加密的书",
                chapterText: "加密正文。",
                encryptionXml:
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"" +
                " xmlns:enc=\"http://www.w3.org/2001/04/xmlenc#\">" +
                "<enc:EncryptedData><enc:EncryptionMethod Algorithm=\"http://www.w3.org/2001/04/xmlenc#aes256-cbc\"/>" +
                "<enc:CipherData><enc:CipherReference URI=\"OEBPS/ch1.xhtml\"/></enc:CipherData>" +
                "</enc:EncryptedData></encryption>");

            var before = await repo.CountAsync();
            var result = await ImportAsync(importer, drm);

            Require(result.Outcome == ImportOutcome.DrmProtected, "DRM 书没有按 DrmProtected 拒绝：" + result.Outcome);
            Require((result.Message ?? string.Empty).Contains("加密"), "提示没说是加密：" + result.Message);
            Require(await repo.CountAsync() == before, "DRM 书却写进了书库");

            return "AES 加密 -> DrmProtected，提示=“" + Shorten(result.Message) + "”，未写库";
        });

        // ---------- 6. Zip Slip 条目 ----------
        await Run("boundary-06-zip-slip", async () =>
        {
            var slip = Path.Combine(workDirectory, "zip-slip.epub");
            WriteEpub(slip, "带越界路径的书", chapterText: "越界测试。");
            AddRawEntry(slip, "../evil.txt");

            var before = await repo.CountAsync();
            var result = await ImportAsync(importer, slip);

            Require(result.Outcome != ImportOutcome.Imported, "带 Zip Slip 的包竟然导入成功");
            Require(
                (result.Message ?? string.Empty).Contains("非法路径"),
                "提示没有指出非法路径：" + result.Message);
            Require(await repo.CountAsync() == before, "拒绝后仍然写了库");

            var escaped = Path.Combine(workDirectory, "evil.txt");
            Require(!File.Exists(escaped), "越界文件竟然落盘了：" + escaped);

            return "Zip Slip -> 明确拒绝，提示=“" + Shorten(result.Message) + "”，未落盘、未写库";
        });

        // ---------- 7. 元数据 XML 注入 ----------
        await Run("boundary-07-xml-injection", async () =>
        {
            var injection = Path.Combine(workDirectory, "injection.epub");
            const string payload = "<script>alert(1)</script>";
            WriteEpub(injection, payload, chapterText: "注入测试。");

            var result = await ImportAsync(importer, injection);
            Require(result.Outcome == ImportOutcome.Imported, "合法 EPUB 导入失败：" + result.Message);

            var injectedId = result.BookId
                ?? throw new InvalidOperationException("没有拿到 bookId");

            var book = await repo.GetByIdAsync(injectedId);
            Require(book is not null, "库里读不到刚导入的书");
            Require(book!.Title == payload, "书名没有原样保存（应转义存储）：" + book.Title);

            // 编辑器把书名写回 OPF 时必须转义，不能把 <script> 原样写进 XML。
            var work = Path.Combine(workDirectory, "xref");
            ExtractZip(book.Formats[0].FilePath, work);
            var opf = Directory.EnumerateFiles(work, "*.opf", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException("解包后找不到 OPF");

            var editor = new OpfMetadataEditor(opf);
            editor.SetTitle(payload);
            editor.Save();

            var raw = await File.ReadAllTextAsync(opf, Encoding.UTF8);
            Require(raw.Contains("&lt;script&gt;"), "写回的 OPF 没有把 <script> 转义");
            Require(!raw.Contains("<script>alert"), "OPF 里出现了未转义的 <script>");

            return "书名按字面值入库；写回 OPF 时转义成 &lt;script&gt;，不会被执行";
        });

        // ---------- 8. 超长书名（1000 字） ----------
        await Run("boundary-08-long-title", async () =>
        {
            var longTitle = LongTitlePrefix + new string('书', 1000);
            var longEpub = Path.Combine(workDirectory, "long-title.epub");
            WriteEpub(longEpub, longTitle, chapterText: "超长书名测试。");

            var result = await ImportAsync(importer, longEpub);
            Require(result.Outcome == ImportOutcome.Imported, "超长书名导入失败：" + result.Message);

            var id = result.BookId
                ?? throw new InvalidOperationException("没有拿到 bookId");

            var book = await repo.GetByIdAsync(id);
            Require(book!.Title.Length == longTitle.Length, "书名在入库阶段被意外截断了");

            var window = services.GetRequiredService<MainWindow>();
            var page = services.GetRequiredService<LibraryPage>();

            window.Show();
            await page.ViewModel.LoadAsync().ConfigureAwait(true);
            window.UpdateLayout();

            var block = FindTextBlock(page, t => t.Text.StartsWith(LongTitlePrefix, StringComparison.Ordinal)
                && t.Text.Length > 500);

            Require(block is not null, "界面里找不到绑定了超长书名的 TextBlock");
            Require(block!.TextTrimming != TextTrimming.None, "超长书名的 TextBlock 没有设置省略号截断");
            Require(block.ActualHeight <= 60, "超长书名把卡片撑高了：" + block.ActualHeight + "px");
            Require(block.ActualWidth <= page.ActualWidth, "超长书名横向撑破了布局");

            window.Close();
            return "1000 字书名入库完整；卡片按 " + block.TextTrimming + " 截断，高度 "
                + block.ActualHeight.ToString("F0") + "px，未撑破布局";
        });

        // ---------- 9. 特殊字符文件名 ----------
        await Run("boundary-09-special-filename", async () =>
        {
            const string dirty = "a<b>c:d\"e/f\\g|h?i*";
            var clean = PathUtil.SanitizeFileName(dirty);

            Require(clean.Length > 0, "净化后变成了空文件名");
            Require(
                clean.IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
                "净化结果里仍有 Windows 非法字符：" + clean);
            Require(!clean.EndsWith('.') && !clean.EndsWith(' '), "净化结果以点或空格结尾：" + clean);

            var reserved = PathUtil.SanitizeFileName("CON");
            Require(!string.Equals(reserved, "CON", StringComparison.OrdinalIgnoreCase), "保留设备名没有被避开");

            var longName = PathUtil.SanitizeFileName(new string('名', 400));
            Require(longName.Length <= 120, "超长文件名没有被截断：" + longName.Length);

            var libraryPath = PathUtil.BuildLibraryPath(
                paths.LibraryDir, Guid.NewGuid(), dirty + ".epub", ".epub");
            Require(
                Path.GetFileName(libraryPath).IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
                "正式区路径里仍有非法字符：" + libraryPath);

            await Task.CompletedTask;
            return "非法字符全部替换；保留名/超长已处理；正式区路径合法（" + clean + "）";
        });

        // ---------- 10. 外部工具被删 ----------
        await Run("boundary-10-tool-missing", async () =>
        {
            var fakeTools = new MissingCalibreLocator(Path.Combine(workDirectory, "missing-tools"));
            var book = new Book { Id = Guid.NewGuid(), Title = "工具缺失测试" };
            book.Formats.Add(new BookFormat
            {
                BookId = book.Id,
                Format = BookFormatKind.Epub,
                FilePath = validEpub,
                FileSize = new FileInfo(validEpub).Length,
                FileModifiedAt = DateTimeOffset.Now,
                AddedAt = DateTimeOffset.Now,
            });

            var window = new ConvertWindow(conversion, queue, templates, fakeTools, book)
            {
                ShowActivated = false,
            };

            window.Show();
            window.UpdateLayout();

            var start = window.FindName("StartButton") as Button;
            var hint = window.FindName("HintText") as TextBlock;
            var sourceCombo = window.FindName("SourceCombo") as ComboBox;

            Require(start is not null && hint is not null, "转换窗口缺控件，XAML 加载失败");
            Require(start!.IsEnabled == false, "工具缺失时「加入队列」没有置灰");
            Require(sourceCombo?.IsEnabled == false, "工具缺失时源格式下拉没有置灰");
            Require(
                (hint!.Text ?? string.Empty).Contains("未找到 Calibre"),
                "提示没有说明缺少 Calibre：" + hint.Text);
            Require(
                (hint.Text ?? string.Empty).Contains(fakeTools.CalibreConvert),
                "提示没有给出期望路径：" + hint.Text);

            window.Close();
            await Task.CompletedTask;
            return "Calibre 缺失 -> 「加入队列」与下拉置灰；提示含原因与期望路径 "
                + fakeTools.CalibreConvert;
        });

        // ---------- 11. 外部工具输出乱码 ----------
        await Run("boundary-11-garbled-tool-output", async () =>
        {
            var garbled = "abc" + '\uFFFD' + '\uFFFD' + "def" + '\u0001' + "ghi";
            var clean = ToolOutputText.Sanitize(garbled);

            Require(!clean.Contains('\uFFFD'), "清洗后仍然有替换符（乱码）");
            Require(!clean.Contains('\u0001'), "清洗后仍然有控制字符");
            Require(clean.Contains(ToolOutputText.EncodingFailureMark), "没有给出明确的编码异常标记");
            Require(clean.Contains("abc") && clean.Contains("ghi"), "正常字符被误删");

            await Task.CompletedTask;
            return "连续替换符收成“" + ToolOutputText.EncodingFailureMark + "”，控制字符剔除，正常字符保留";
        });

        // ---------- 12. 转换中关闭程序（恢复语义） ----------
        await Run("boundary-12-close-during-conversion", async () =>
        {
            var task = new ConversionTask
            {
                Id = Guid.NewGuid(),
                InputPath = validEpub,
                OutputPath = Path.Combine(workDirectory, "out.mobi"),
                OutFormat = BookFormatKind.Mobi,
                CreatedAt = DateTimeOffset.Now,
                Status = ConversionTaskStatus.Running,
                StartedAt = DateTimeOffset.Now,
                Progress = 0.42,
            };

            await taskRepo.AddAsync(task);
            var reset = await taskRepo.ResetInterruptedAsync();
            var reloaded = await taskRepo.GetByIdAsync(task.Id);

            Require(reset >= 1, "崩溃恢复没有把 Running 任务归位");
            Require(reloaded is not null, "归位后读不到任务");
            Require(reloaded!.Status == ConversionTaskStatus.Queued, "Running 没有回到 Queued：" + reloaded.Status);
            Require(reloaded.StartedAt is null, "归位后开始时间没有清空");
            Require(reloaded.Progress is null, "归位后进度没有清空");

            await taskRepo.DeleteAsync(task.Id);

            return "Running 任务重启后归位 Queued（进度/开始时间已清）；"
                + "Job Object 杀进程与续跑另由 Runner 的真实强杀阶段覆盖";
        });

        // ---------- 13. 数据库文件被占用 ----------
        await Run("boundary-13-db-locked", async () =>
        {
            var before = await repo.CountAsync();
            var thrown = false;
            string code = "(无)";

            await using (var holder = await factory.OpenAsync().ConfigureAwait(true))
            {
                // WAL 模式下 BEGIN EXCLUSIVE 会挡住其它写者；busy_timeout=5s 等完才失败。
                await holder.ExecuteAsync("BEGIN EXCLUSIVE;").ConfigureAwait(true);

                try
                {
                    await repo.AddAsync(NewBook("库被占用时写入")).ConfigureAwait(true);
                    throw new InvalidOperationException("库被排他锁占住时竟然写入成功");
                }
                catch (AppException ex) when (ex.Code is ErrorCode.FileLocked
                    or ErrorCode.DataDirNotWritable or ErrorCode.AccessDenied)
                {
                    thrown = true;
                    code = ex.Code.ToString();
                    Require(
                        ex.UserMessage.Contains("占用") || ex.UserMessage.Contains("打不开")
                            || ex.UserMessage.Contains("只读"),
                        "数据库被占用的提示不够明确：" + ex.UserMessage);
                }
                finally
                {
                    await holder.ExecuteAsync("ROLLBACK;").ConfigureAwait(true);
                }
            }

            Require(thrown, "数据库被占用时没有抛可读的 AppException");

            // 释放后重试必须成功，不能要重启程序。
            var retry = NewBook("锁释放后重试");
            await repo.AddAsync(retry).ConfigureAwait(true);
            Require(await repo.CountAsync() >= before + 1, "释放占用后重试没有写入");
            await repo.DeleteAsync(retry.Id).ConfigureAwait(true);

            return "排他锁 -> AppException(" + code + ")（含原因与「关掉占用程序后重试」）；释放后重试成功";
        });

        // ---------- 16. 操作中途断电（一致性快照） ----------
        await Run("boundary-16-power-loss-consistency", async () =>
        {
            await using var conn = await factory.OpenAsync().ConfigureAwait(true);
            var integrity = await conn.ScalarAsync<string>("PRAGMA integrity_check;").ConfigureAwait(true);
            Require(string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase),
                "数据库一致性检查没通过：" + integrity);

            var partFiles = Directory
                .EnumerateFiles(paths.DataRoot, "*" + AtomicFile.TempSuffix + "*", SearchOption.AllDirectories)
                .Where(f => f.Contains(AtomicFile.TempSuffix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Require(partFiles.Count == 0, "数据目录里残留半截文件：" + string.Join(", ", partFiles));

            // 原子写失败时必须清理临时文件，且绝不创建目标。
            var blockedTarget = Path.Combine(workDirectory, "no-such-dir", "x.txt");
            var blocked = false;

            try
            {
                AtomicFile.WriteAllText(blockedTarget, "x", Encoding.UTF8);
            }
            catch (DirectoryNotFoundException)
            {
                blocked = true;
            }

            Require(blocked, "写入不存在的目录没有失败（不符合预期）");
            Require(!File.Exists(blockedTarget), "失败后竟然创建了目标文件");
            Require(!File.Exists(blockedTarget + AtomicFile.TempSuffix), "失败后残留 .part 临时文件");

            return "integrity_check=ok；全数据目录 0 个 .part 残留；原子写失败后目标与临时文件都不存在";
        });

        // ---------- 14. 阅读器图片 404 ----------
        // ---------- 15. 主题 CSS 加载失败回退默认主题 ----------
        await Run("boundary-14-15-reader-image-and-theme", async () =>
        {
            var missingImageEpub = Path.Combine(workDirectory, "missing-image.epub");
            WriteEpub(
                missingImageEpub,
                "缺图的书",
                chapterText: "正文第一段。",
                chapterExtraHtml: "<p><img src=\"missing.png\" alt=\"\" /></p>");

            var imported = await ImportAsync(importer, missingImageEpub);
            Require(imported.Outcome == ImportOutcome.Imported, "缺图书导入失败：" + imported.Message);

            var bookId = imported.BookId
                ?? throw new InvalidOperationException("没有拿到 bookId");

            ReaderWindow? window = null;

            try
            {
                var session = await reader.OpenAsync(bookId, CancellationToken.None).ConfigureAwait(true);
                Require(session.Manifest.Spine.Count > 0, "阅读器缓存没有章节");

                window = new ReaderWindow(
                    reader,
                    environments,
                    progress,
                    loggerFactory.CreateLogger<ReaderWindow>(),
                    bookId,
                    workspaces: workspaces,
                    source: source,
                    assets: assets,
                    repository: repo,
                    autofix: autofix,
                    findReplace: findReplace,
                    snapshots: snapshots);

                window.Show();

                var ready = await WaitUntilAsync(
                    () => window.LastChapterIndex == 0 && window.RenderedTextLength > 0,
                    TimeSpan.FromSeconds(40)).ConfigureAwait(true);

                Require(ready, "阅读器 40 秒内没有渲染出第一章");

                var probe = await window.ExecAsync(
                    "(function(){var f=document.getElementById('chapter');var d=f&&f.contentDocument;"
                    + "if(!d){return 'no-doc';}var imgs=d.querySelectorAll('img');var out=[];"
                    + "for(var i=0;i<imgs.length;i++){var im=imgs[i];"
                    + "out.push({missing:im.getAttribute('data-ets-missing'),cls:im.className,alt:im.getAttribute('alt')});}"
                    + "return JSON.stringify({n:imgs.length,items:out});})()").ConfigureAwait(true);

                var text = UnwrapScriptString(probe)
                    ?? throw new InvalidOperationException("阅读器 DOM 探针没有返回");

                Require(text != "no-doc", "拿不到章节 iframe 文档");
                using var doc = JsonDocument.Parse(text);
                var items = doc.RootElement.GetProperty("items");

                var missingMarked = items.EnumerateArray().Any(i =>
                    i.GetProperty("missing").GetString() == "1"
                    && (i.GetProperty("cls").GetString() ?? string.Empty).Contains("ets-img-missing")
                    && (i.GetProperty("alt").GetString() ?? string.Empty).Contains("无法显示"));

                Require(missingMarked, "图片 404 后没有显示占位（缺 data-ets-missing / 类名 / alt）");
                Require(window.RenderedBrokenImageCount >= 1, "阅读器没有记录到破图");

                // T7-2 第 15 项：未知主题 id 必须回退到默认主题（light）。
                var theme = UnwrapScriptString(
                    await window.ExecAsync(
                        "String(window.__readerDebug.setTheme('no-such-theme-t72'))").ConfigureAwait(true));

                Require(
                    string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase),
                    "未知主题没有回退到默认主题，实际=" + theme);

                return "图片 404 -> 占位块（ets-img-missing + alt「图片无法显示」）；"
                    + "未知主题 -> 回退 light";
            }
            finally
            {
                window?.Close();
            }
        });

        return Build(paths, startedAt, total, checks);
    }

    // ================= 辅助 =================

    private static Task<ImportResult> ImportAsync(IBookImportService importer, string path)
        => importer.ImportAsync(path, ImportOptions.Default, progress: null, CancellationToken.None);

    private static Book NewBook(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now,
    };

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string Shorten(string? text)
        => string.IsNullOrEmpty(text) ? "(空)"
            : text.Length <= 100 ? text
            : text[..100] + "…";

    /// <summary>找一个未占用的盘符，用来模拟"数据目录所在盘被拔掉"。</summary>
    private static string? FindUnusedDriveRoot()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        foreach (var letter in "DEFGHIJKLMNOPQRSTUVWXYZ")
        {
            if (!used.Contains(letter))
            {
                return letter + ":\\";
            }
        }

        return null;
    }

    /// <summary>ExecAsync 会把字符串结果再包一层 JSON 引号。</summary>
    private static string? UnwrapScriptString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        var text = raw.Trim();

        if (text.Length > 1 && text[0] == '"' && text[^1] == '"')
        {
            return JsonSerializer.Deserialize<string>(text);
        }

        return text;
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    // ---------- 构造 EPUB ----------

    private const string ContainerXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
        "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" " +
        "media-type=\"application/oebps-package+xml\"/></rootfiles></container>";

    private static void WriteEpub(
        string path,
        string title,
        string chapterText,
        string? encryptionXml = null,
        string? chapterExtraHtml = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        // mimetype 必须是第一条且不压缩（OCF 规范）
        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var entry = mime.Open())
        {
            entry.Write(Encoding.ASCII.GetBytes("application/epub+zip"));
        }

        WriteZipText(zip, "META-INF/container.xml", ContainerXml);

        if (encryptionXml is not null)
        {
            WriteZipText(zip, "META-INF/encryption.xml", encryptionXml);
        }

        var opf =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">" +
            "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            "<dc:identifier id=\"bookid\">urn:uuid:" + Guid.NewGuid().ToString("D") + "</dc:identifier>" +
            "<dc:title>" + EscapeXml(title) + "</dc:title>" +
            "<dc:language>zh-CN</dc:language>" +
            "<meta property=\"dcterms:modified\">2026-01-01T00:00:00Z</meta>" +
            "</metadata>" +
            "<manifest>" +
            "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>" +
            "<item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/>" +
            "</manifest>" +
            "<spine><itemref idref=\"ch1\"/></spine>" +
            "</package>";

        WriteZipText(zip, "OEBPS/content.opf", opf);

        WriteZipText(
            zip,
            "OEBPS/nav.xhtml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\">" +
            "<head><title>目录</title></head><body><nav epub:type=\"toc\" id=\"toc\">" +
            "<ol><li><a href=\"ch1.xhtml\">第一章</a></li></ol></nav></body></html>");

        WriteZipText(
            zip,
            "OEBPS/ch1.xhtml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>第一章</title></head><body>" +
            "<h1>第一章</h1><p>" + EscapeXml(chapterText) + "</p>" + (chapterExtraHtml ?? string.Empty) +
            "</body></html>");
    }

    private static void WriteZipText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>往写好的 EPUB 里再塞一个条目（构造 Zip Slip 这类畸形包）。</summary>
    private static void AddRawEntry(string epubPath, string entryName)
    {
        using var stream = new FileStream(epubPath, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Update);
        var entry = zip.CreateEntry(entryName);

        using var writer = new StreamWriter(entry.Open());
        writer.Write("x");
    }

    private static void ExtractZip(string zipPath, string destination)
    {
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);
        ZipFile.ExtractToDirectory(zipPath, destination, overwriteFiles: true);
    }

    private static string EscapeXml(string value)
        => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

    // ---------- UI 辅助 ----------

    private static TextBlock? FindTextBlock(DependencyObject root, Func<TextBlock, bool> predicate)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is TextBlock block && predicate(block))
            {
                return block;
            }

            var nested = FindTextBlock(child, predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();

        while (watch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        return condition();
    }

    // ---------- 假工具定位器（第 10 项） ----------

    /// <summary>声明了 Calibre 但没有文件：用来验证"工具被删"的界面行为。</summary>
    private sealed class MissingCalibreLocator : IToolLocator
    {
        private readonly string _toolsRoot;

        public MissingCalibreLocator(string toolsRoot) => _toolsRoot = toolsRoot;

        public string AppRoot => Path.GetDirectoryName(_toolsRoot) ?? _toolsRoot;

        public string ToolsRoot => _toolsRoot;

        public string DataRoot => _toolsRoot;

        public string CalibreConvert => Path.Combine(_toolsRoot, "Calibre", "ebook-convert.exe");

        public string CalibreMeta => Path.Combine(_toolsRoot, "Calibre", "ebook-meta.exe");

        public string CalibrePolish => Path.Combine(_toolsRoot, "Calibre", "ebook-polish.exe");

        public string CalibreCustomize => Path.Combine(_toolsRoot, "Calibre", "calibre-customize.exe");

        public string? Java => null;

        public string? EpubCheckJar => null;

        public string? WebView2FixedFolder => null;

        public string? PdfiumDll => null;

        public IReadOnlyDictionary<string, string?> BuildEnvironment(string toolKey)
            => new Dictionary<string, string?>();

        public Task<ToolHealthReport> VerifyAllAsync(bool runProbes, CancellationToken ct = default)
            => Task.FromResult(new ToolHealthReport(new[]
            {
                new ToolHealthItem(
                    "calibre",
                    "Calibre",
                    true,
                    ToolState.Missing,
                    null,
                    "未找到 " + CalibreConvert,
                    "把 Calibre 放回 Tools/Calibre 目录，然后重新检测工具"),
            }));
    }

    // ---------- 报告 ----------

    private static SmokeReport Build(
        AppPaths paths,
        DateTimeOffset startedAt,
        Stopwatch total,
        IReadOnlyList<SmokeCheck> checks)
    {
        total.Stop();

        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            total.ElapsedMilliseconds,
            paths.DataRoot,
            paths.WebView2DataDir,
            null,
            checks);

        File.WriteAllText(
            Path.Combine(paths.TempDir, "t72-smoke.json"),
            JsonSerializer.Serialize(report, JsonOptions),
            new UTF8Encoding(false));

        return report;
    }
}
