using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Services.Editing;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Editing;

/// <summary>
/// T3-1 EPUB 工作区的验收测试。对应执行计划的 5 条完成判据：
///   正常 EPUB 解压完整 / 有 encryption.xml 明确拒绝 / Zip Slip 条目被拒 /
///   50 MB 解压 &lt;= 3 秒 / 放弃编辑后会话目录被清理。
/// 全部走真实文件系统与真实 zip，不 mock  这条链路的价值就在"真的解压了、真的没落坏文件"。
/// </summary>
public sealed class EpubWorkspaceTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly EpubWorkspaceFactory _factory;

    public EpubWorkspaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ets-ws-" + Guid.NewGuid().ToString("N"));
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

        _factory = new EpubWorkspaceFactory(
            _paths, SystemClock.Instance, NullLogger<EpubWorkspaceFactory>.Instance);
    }

    // ---------- 判据 1：正常 EPUB 解压完整 ----------

    [Fact]
    public async Task Open_正常EPUB_工作区解压完整并写出会话描述()
    {
        var source = BuildEpub(new EpubBuilder
        {
            ChapterCount = 3,
            CoverBytes = EpubBuilder.MakeJpeg(),
        });

        await using var ws = await _factory.OpenAsync(new WorkspaceOpenRequest { EpubPath = source });

        // 会话目录 = Workspaces/{sessionId}
        Assert.Equal(ws.SessionId, Path.GetFileName(ws.WorkDir));
        Assert.StartsWith(
            Path.GetFullPath(_paths.WorkspacesDir), Path.GetFullPath(ws.WorkDir));

        // 结构文件、OPF、章节、资源一个不少
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "mimetype")));
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "META-INF", "container.xml")));
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "OEBPS", "content.opf")));
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "OEBPS", "ch1.xhtml")));
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "OEBPS", "ch3.xhtml")));
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "OEBPS", "cover.jpg")));

        // 会话描述字段正确
        Assert.Equal("OEBPS/content.opf", ws.Session.OpfPath);
        Assert.Equal("3.0", ws.Session.EpubVersion);
        Assert.Empty(ws.Session.ChangedFiles);
        Assert.False(ws.Session.HasEncryption);
        Assert.Equal(source, ws.Session.SourcePath);

        // .session.json 真的落了盘，且能被恢复
        var sessionFile = Path.Combine(ws.WorkDir, ".session.json");
        Assert.True(File.Exists(sessionFile));
        var json = await File.ReadAllTextAsync(sessionFile);
        Assert.Contains("\"sessionId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourcePath\"", json, StringComparison.Ordinal);

        var resumed = await _factory.ResumeAsync(ws.SessionId);
        Assert.NotNull(resumed);
        Assert.Equal(source, resumed!.Session.SourcePath);
        Assert.True(resumed.Session.OpfPath == "OEBPS/content.opf");

        var resumable = _factory.ListResumable();
        Assert.Single(resumable);
        Assert.Equal(ws.SessionId, resumable[0].SessionId);
    }

    [Fact]
    public async Task Resume_旧会话没有基线_自动补一份全量基线且不重复补()
    {
        var source = BuildEpub(new EpubBuilder { ChapterCount = 2 });
        var snapshots = new WorkspaceSnapshotService();
        var factory = new EpubWorkspaceFactory(
            _paths, SystemClock.Instance, NullLogger<EpubWorkspaceFactory>.Instance, snapshots);

        var opened = await factory.OpenAsync(new WorkspaceOpenRequest { EpubPath = source });

        try
        {
            // 打开时本来有一份基线；删掉它，模拟"T5-4 之前建的旧会话"。
            var snapshotRoot = Path.Combine(opened.WorkDir, ".snapshots");
            Assert.True(Directory.Exists(snapshotRoot));
            Directory.Delete(snapshotRoot, recursive: true);

            var resumed = await factory.ResumeAsync(opened.SessionId);
            Assert.NotNull(resumed);

            var list = await snapshots.ListAsync(resumed!);
            var baseline = Assert.Single(list, snapshot => snapshot.Kind == WorkspaceSnapshotKinds.Initial);

            Assert.True(baseline.IsFull);
            Assert.True(Directory.Exists(snapshots.SnapshotDirectory(resumed!, baseline.Id)));

            // 再恢复一次：不该多出第二份基线（幂等）。
            var again = await factory.ResumeAsync(opened.SessionId);
            Assert.NotNull(again);

            var listAgain = await snapshots.ListAsync(again!);
            Assert.Single(listAgain, snapshot => snapshot.Kind == WorkspaceSnapshotKinds.Initial);
        }
        finally
        {
            await opened.DiscardAsync();
        }
    }

    [Fact]
    public async Task Open_EPUB2的书_会话记录版本2()
    {
        var source = BuildEpub(new EpubBuilder { IsEpub3 = false });

        await using var ws = await _factory.OpenAsync(new WorkspaceOpenRequest { EpubPath = source });

        Assert.Equal("2.0", ws.Session.EpubVersion);
    }

    // ---------- 判据 2：有 encryption.xml 明确拒绝 ----------

    [Fact]
    public async Task Open_真有DRM_抛DrmDetected且不留半成品会话()
    {
        var source = BuildEpub(new EpubBuilder { MarkEncrypted = true });

        var ex = await Assert.ThrowsAsync<AppException>(
            () => _factory.OpenAsync(new WorkspaceOpenRequest { EpubPath = source }));

        Assert.Equal(ErrorCode.DrmDetected, ex.Code);
        Assert.Contains("不处理 DRM", ex.UserMessage, StringComparison.Ordinal);

        // 一个会话目录都不许留下
        Assert.Empty(Directory.GetDirectories(_paths.WorkspacesDir));
    }

    [Fact]
    public async Task Open_只有字体混淆_放行并记录HasEncryption()
    {
        var source = BuildEpub(new EpubBuilder
        {
            EncryptionXmlOverride = ObfuscationXml(
                "http://www.idpf.org/2008/embedding", "OEBPS/fonts/body.ttf"),
        });

        await using var ws = await _factory.OpenAsync(new WorkspaceOpenRequest { EpubPath = source });

        Assert.True(ws.Session.HasEncryption);
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "META-INF", "encryption.xml")));
    }

    // ---------- 判据 3：Zip Slip 条目被拒 ----------

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\..\\evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("OEBPS/../../evil.txt")]
    public async Task Open_含非法路径条目_整包拒绝且一个字节都不落(string entryName)
    {
        var source = BuildEpub(new EpubBuilder());
        AppendBytes(source, entryName, Encoding.UTF8.GetBytes("x"));

        var ex = await Assert.ThrowsAsync<AppException>(
            () => _factory.OpenAsync(new WorkspaceOpenRequest { EpubPath = source }));

        Assert.Equal(ErrorCode.UnsafeArchiveEntry, ex.Code);
        Assert.Contains("压缩包有非法路径", ex.UserMessage, StringComparison.Ordinal);

        // 整包拒绝：没会话目录，也没逃逸出去的文件
        Assert.Empty(Directory.GetDirectories(_paths.WorkspacesDir));
        Assert.False(File.Exists(Path.Combine(_paths.WorkspacesDir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    // ---------- 判据 4：50 MB 解压 <= 3 秒 ----------

    [Fact]
    public async Task Open_50MB的EPUB_解压在3秒内完成()
    {
        const long fiftyMb = 50L * 1024 * 1024;
        var source = BuildEpub(new EpubBuilder { ChapterCount = 2 });
        AppendZeros(source, "OEBPS/big.bin", fiftyMb);

        var sw = Stopwatch.StartNew();
        await using var ws = await _factory.OpenAsync(new WorkspaceOpenRequest { EpubPath = source });
        sw.Stop();

        var extracted = Path.Combine(ws.WorkDir, "OEBPS", "big.bin");
        Assert.True(File.Exists(extracted));
        Assert.Equal(fiftyMb, new FileInfo(extracted).Length);

        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(3),
            $"50MB 解压用了 {sw.Elapsed.TotalSeconds:F2}s，判据是 <= 3s");
    }

    // ---------- 判据 5：放弃编辑后清理会话目录 ----------

    [Fact]
    public async Task Discard_删除会话目录且原文件分毫未动()
    {
        var source = BuildEpub(new EpubBuilder { ChapterCount = 2 });
        var beforeBytes = await File.ReadAllBytesAsync(source);

        var ws = await _factory.OpenAsync(new WorkspaceOpenRequest { EpubPath = source });
        var workDir = ws.WorkDir;
        Assert.True(Directory.Exists(workDir));

        await ws.DiscardAsync();

        Assert.False(Directory.Exists(workDir));
        Assert.Empty(Directory.GetDirectories(_paths.WorkspacesDir));

        // 原文件一个字节都没变
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(source));

        // 幂等；Dispose 不再报错
        await ws.DiscardAsync();
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Resume_残留会话不存在_返回null而不是抛异常()
    {
        var resumed = await _factory.ResumeAsync("deadbeef");
        Assert.Null(resumed);
    }

    [Fact]
    public async Task Resume_会话号带路径穿越_拒绝()
    {
        await Assert.ThrowsAsync<AppException>(() => _factory.ResumeAsync("../escape"));
    }

    // ---------- 无效输入 ----------

    [Fact]
    public async Task Open_文件不存在_抛FileNotFound()
    {
        var ex = await Assert.ThrowsAsync<AppException>(
            () => _factory.OpenAsync(new WorkspaceOpenRequest
            {
                EpubPath = Path.Combine(_root, "nope.epub"),
            }));

        Assert.Equal(ErrorCode.FileNotFound, ex.Code);
    }

    [Fact]
    public async Task Open_不是EPUB的zip_抛EpubInvalid且不留目录()
    {
        var source = Path.Combine(_root, "not-epub.zip");
        using (var fs = new FileStream(source, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("readme.txt");
            using var w = new StreamWriter(e.Open());
            w.Write("hello");
        }

        var ex = await Assert.ThrowsAsync<AppException>(
            () => _factory.OpenAsync(new WorkspaceOpenRequest { EpubPath = source }));

        Assert.Equal(ErrorCode.EpubInvalid, ex.Code);
        Assert.Empty(Directory.GetDirectories(_paths.WorkspacesDir));
    }

    // ---------- 造样本 ----------

    private string BuildEpub(EpubBuilder builder)
    {
        var dir = Path.Combine(_root, "books");
        Directory.CreateDirectory(dir);
        return builder.WriteTo(Path.Combine(dir, Guid.NewGuid().ToString("N") + ".epub"));
    }

    private static void AppendBytes(string epubPath, string entryName, byte[] content)
    {
        using var fs = new FileStream(epubPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Update);
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static void AppendZeros(string epubPath, string entryName, long length)
    {
        using var fs = new FileStream(epubPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Update);
        var entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        var buffer = new byte[1 << 20];
        var remaining = length;
        while (remaining > 0)
        {
            var n = (int)Math.Min(buffer.Length, remaining);
            stream.Write(buffer, 0, n);
            remaining -= n;
        }
    }

    private static string ObfuscationXml(string algorithm, string uri) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"" +
        " xmlns:enc=\"http://www.w3.org/2001/04/xmlenc#\">" +
        "<enc:EncryptedData>" +
        "<enc:EncryptionMethod Algorithm=\"" + algorithm + "\"/>" +
        "<enc:CipherData><enc:CipherReference URI=\"" + uri + "\"/></enc:CipherData>" +
        "</enc:EncryptedData></encryption>";

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
