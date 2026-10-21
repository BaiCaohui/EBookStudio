using System.Globalization;
using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Services.Editing;
using Xunit;

namespace EbookToolSuite.Services.Tests.Editing;

/// <summary>
/// T5-4 编辑会话快照（文件级）。覆盖：打开工作区的全量基线、增量只含点名文件、
/// "还原=撤销那一次操作"、还原前自动反向快照、全部替换前快照+一键撤销、
/// 按类型清理（保存前 10 / 替换前 1）、路径穿越拒收。走真实文件系统（本仓库约定：不 mock）。
/// </summary>
public sealed class WorkspaceSnapshotServiceTests
{
    private const string Entry1 = "OEBPS/Text/ch1.xhtml";
    private const string Entry2 = "OEBPS/Text/ch2.xhtml";

    [Fact]
    public async Task 打开工作区的全量基线_含所有文件_且能整份还原()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, "<html>1</html>");
            Write(root, Entry2, "<html>2</html>");
            Write(root, "mimetype", "application/epub+zip");
            var snapshots = new WorkspaceSnapshotService();

            var full = await snapshots.TakeFullAsync(ws);

            Assert.True(full.IsFull);
            Assert.Equal(WorkspaceSnapshotKinds.Initial, full.Kind);
            Assert.Equal(3, full.EntryCount);
            Assert.Single(await snapshots.ListAsync(ws));

            // 弄乱工作区：改一个、删一个、加一个
            Write(root, Entry1, "<html>改过</html>");
            File.Delete(Full(root, Entry2));
            Write(root, "OEBPS/Text/ch3.xhtml", "<html>新增</html>");

            var restored = await RestoreAsync(ws, snapshots, full.Id);

            Assert.True(restored.Ok, restored.Message);
            Assert.Equal("<html>1</html>", Read(root, Entry1));
            Assert.True(File.Exists(Full(root, Entry2)));
            Assert.False(File.Exists(Full(root, "OEBPS/Text/ch3.xhtml")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task 保存前增量快照_只含点名的文件()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, "甲");
            Write(root, Entry2, "乙");
            var snapshots = new WorkspaceSnapshotService();

            var inc = await TakeAsync(ws, snapshots, Entry1, WorkspaceSnapshotKinds.Save);

            Assert.False(inc.IsFull);
            Assert.Equal(WorkspaceSnapshotKinds.Save, inc.Kind);
            Assert.Single(inc.Entries);
            Assert.Equal(Entry1, inc.Entries[0].RelativePath);
            Assert.False(string.IsNullOrEmpty(inc.Entries[0].Sha256));
            Assert.True(inc.TotalBytes > 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task 还原增量快照_等于撤销那一次操作()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            var snapshots = new WorkspaceSnapshotService();

            Write(root, Entry1, "A");
            var save1 = await TakeAsync(ws, snapshots, Entry1, WorkspaceSnapshotKinds.Save);
            Write(root, Entry1, "B");
            var save2 = await TakeAsync(ws, snapshots, Entry1, WorkspaceSnapshotKinds.Save);
            Write(root, Entry1, "C");

            Assert.True((await RestoreAsync(ws, snapshots, save2.Id)).Ok);
            Assert.Equal("B", Read(root, Entry1));

            Assert.True((await RestoreAsync(ws, snapshots, save1.Id)).Ok);
            Assert.Equal("A", Read(root, Entry1));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task 还原前自动创建反向快照_能再还原它退回来()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            var snapshots = new WorkspaceSnapshotService();

            Write(root, Entry1, "A");
            var save1 = await TakeAsync(ws, snapshots, Entry1, WorkspaceSnapshotKinds.Save);
            Write(root, Entry1, "B");

            var first = await RestoreAsync(ws, snapshots, save1.Id);

            Assert.True(first.Ok, first.Message);
            Assert.False(string.IsNullOrEmpty(first.SafetySnapshotId));
            Assert.Equal("A", Read(root, Entry1));

            // 反悔：还原"还原前的那份"，应该回到 B
            var back = await RestoreAsync(ws, snapshots, first.SafetySnapshotId!);

            Assert.True(back.Ok, back.Message);
            Assert.Equal("B", Read(root, Entry1));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task 全部替换前的快照_能一键撤销上一次替换()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p>甲甲</p>"));
            var snapshots = new WorkspaceSnapshotService();
            var replace = new ChapterFindReplaceService(new ChapterSourceService(), snapshots: snapshots);

            var plan = replace.Preview(
                ws,
                new[] { new FindReplaceChapter(0, Entry1, "第 1 章") },
                new FindReplaceOptions("甲", "乙"));
            Assert.True(plan.Ok);

            var outcome = replace.Apply(ws, plan);

            Assert.True(outcome.Ok);
            Assert.False(string.IsNullOrEmpty(outcome.SnapshotId));
            Assert.Contains("乙", Read(root, Entry1));

            var before = await snapshots.FindLatestAsync(ws, WorkspaceSnapshotKinds.ReplaceBefore);

            Assert.NotNull(before);
            Assert.Equal(outcome.SnapshotId, before!.Id);

            var undo = await RestoreAsync(ws, snapshots, outcome.SnapshotId!);

            Assert.True(undo.Ok);
            Assert.Contains("甲", Read(root, Entry1));
            Assert.DoesNotContain("乙", Read(root, Entry1));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task 清理_保存前留最近10份_替换前只留1份()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, "A");
            var snapshots = new WorkspaceSnapshotService();

            var first = await TakeAsync(ws, snapshots, Entry1, WorkspaceSnapshotKinds.Save);
            for (var i = 0; i < 11; i++)
            {
                await TakeAsync(ws, snapshots, Entry1, WorkspaceSnapshotKinds.Save);
            }

            var saves = (await snapshots.ListAsync(ws))
                .Where(snapshot => snapshot.Kind == WorkspaceSnapshotKinds.Save)
                .ToList();

            Assert.Equal(10, saves.Count);
            Assert.DoesNotContain(saves, snapshot => snapshot.Id == first.Id);

            var oldReplace = await TakeAsync(ws, snapshots, Entry1, WorkspaceSnapshotKinds.ReplaceBefore);
            var newReplace = await TakeAsync(ws, snapshots, Entry1, WorkspaceSnapshotKinds.ReplaceBefore);
            var replaces = (await snapshots.ListAsync(ws))
                .Where(snapshot => snapshot.Kind == WorkspaceSnapshotKinds.ReplaceBefore)
                .ToList();

            Assert.Single(replaces);
            Assert.Equal(newReplace.Id, replaces[0].Id);
            Assert.NotEqual(oldReplace.Id, replaces[0].Id);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task 快照路径越出工作区_被拒()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, "A");
            var snapshots = new WorkspaceSnapshotService();

            var error = await Assert.ThrowsAsync<AppException>(
                () => snapshots.TakeIncrementalAsync(ws, new[] { "../evil.xhtml" }));

            Assert.Equal(ErrorCode.UnsafeArchiveEntry, error.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task 编辑20个章节_增量快照总占用不超过基线的一半()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            var snapshots = new WorkspaceSnapshotService();

            /* 200 章的书，每章 ~2KB。改 20 章 = 全书的 10%：
               增量快照只存这 20 章，占用必须远小于基线的一半（计划 T5-4 判据 5）。
               注：基线本身不计入"编辑增量"，它是打开工作区时的那份原始文件副本。 */
            var chapterPaths = new List<string>();

            for (var i = 0; i < 200; i++)
            {
                var entry = "OEBPS/Text/ch" + i.ToString("D3", CultureInfo.InvariantCulture) + ".xhtml";
                Write(root, entry, "<html><body>" + new string('甲', 2000) + "</body></html>");
                chapterPaths.Add(entry);
            }

            var full = await snapshots.TakeFullAsync(ws);

            for (var i = 0; i < 20; i++)
            {
                await TakeAsync(ws, snapshots, chapterPaths[i], WorkspaceSnapshotKinds.Save);
            }

            var incremental = snapshots.IncrementalBytes(ws);
            var budget = full.TotalBytes / 2;

            Assert.True(
                incremental <= budget,
                "20 章的增量快照占用 " + incremental + " 字节，超过基线一半 " + budget + " 字节");
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---------- 工具 ----------

    private static Task<WorkspaceSnapshot> TakeAsync(
        TempWorkspace ws, WorkspaceSnapshotService snapshots, string entry, string kind)
        => snapshots.TakeIncrementalAsync(ws, new[] { entry }, kind);

    private static Task<WorkspaceRestoreResult> RestoreAsync(
        TempWorkspace ws, WorkspaceSnapshotService snapshots, string snapshotId)
        => snapshots.RestoreAsync(ws, snapshotId);

    private static string Chapter(string body, string title = "书名")
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<html xmlns=\"http://www.w3.org/1999/xhtml\">\r\n"
            + "<head><title>" + title + "</title></head>\r\n"
            + "<body>" + body + "</body>\r\n</html>";

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ets-snap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Full(string root, string entry)
        => Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar));

    private static void Write(string root, string entry, string text)
    {
        var path = Full(root, entry);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Read(string root, string entry) => File.ReadAllText(Full(root, entry));

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录删不掉不该让测试失败
        }
    }

    /// <summary>临时工作区：一个目录 + 一份会话。</summary>
    private sealed class TempWorkspace(string root) : IEpubWorkspace
    {
        public string SessionId => Session.SessionId;

        public string WorkDir { get; } = root;

        public WorkspaceSession Session { get; } = new()
        {
            SessionId = "t" + Guid.NewGuid().ToString("N")[..6],
            BookId = Guid.NewGuid(),
            FormatId = Guid.NewGuid(),
            SourcePath = Path.Combine(root, "book.epub"),
            OpenedAt = DateTimeOffset.Now,
        };

        public Task DiscardAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}