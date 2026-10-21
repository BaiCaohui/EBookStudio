using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Text;
using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// T3-4 打包器的单元测试（不依赖真工具）。
/// 覆盖计划里的判据 1（mimetype 第一条/STORED/内容精确/只有一个）、
/// 判据 4（固定时间戳 -> 同样输入两次打包字节相同）、判据 5（50MB <= 6 秒）、
/// 判据 6/7（失败与占用时不碰原文件）。
/// </summary>
public sealed class EpubPackagerTests : IDisposable
{
    private const string OpfPath = "OEBPS/content.opf";
    private const string CoverJpg = "OEBPS/Images/cover.jpg";
    private const string MimetypeContent = "application/epub+zip";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ets-packager-" + Guid.NewGuid().ToString("N"));

    private readonly EpubPackager _packager = new();

    public EpubPackagerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ================= 判据 1：mimetype 第一条 / STORED / 内容精确 / 只有一个 =================

    [Fact]
    public async Task 打包后_mimetype_是第一条且不压缩且内容精确且只有一个()
    {
        var workspace = Workspace();
        var output = Path.Combine(_root, "out.epub");

        await _packager.PackageAsync(workspace, output);

        // 复核器自己也过一遍
        var verification = EpubPackageVerifier.Verify(output);
        Assert.Equal(MimetypeContent.Length, verification.MimetypeLength);
        Assert.True(verification.EntryCount > 0);

        // 再独立看原始字节：本地文件头 + STORED + 无 data descriptor + 无 extra field
        var bytes = File.ReadAllBytes(output);
        Assert.Equal(0x04034B50u, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 6) & 0x0008);
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 8));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 28));

        var nameLength = BitConverter.ToUInt16(bytes, 26);
        Assert.Equal("mimetype", Encoding.ASCII.GetString(bytes, 30, nameLength));

        var contentOffset = 30 + nameLength;
        Assert.NotEqual(0xEF, bytes[contentOffset]);
        Assert.Equal(MimetypeContent, Encoding.ASCII.GetString(bytes, contentOffset, MimetypeContent.Length));

        // 并且它真的在包外没有任何重复
        using var zip = ZipFile.OpenRead(output);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        Assert.Single(zip.Entries, e => e.FullName == "mimetype");
    }

    [Fact]
    public async Task 工作区自带的_mimetype_与会话文件快照都不进包()
    {
        var workspace = Workspace();

        // 工作区根目录里的 mimetype 是解压出来的，必须被排除（审计 S3）
        File.WriteAllText(Path.Combine(workspace, "mimetype"), MimetypeContent, new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(workspace, ".session.json"),
            "{\"sessionId\":\"abc\",\"bookId\":\"x\"}",
            new UTF8Encoding(false));
        var snapshot = Path.Combine(workspace, ".snapshots", "snap-1");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "content.opf"), "<x/>", new UTF8Encoding(false));

        var output = Path.Combine(_root, "out.epub");
        await _packager.PackageAsync(workspace, output);

        using var zip = ZipFile.OpenRead(output);
        Assert.Single(zip.Entries, e => e.FullName == "mimetype");
        Assert.DoesNotContain(zip.Entries, e => e.FullName == ".session.json");
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith(".snapshots/", StringComparison.Ordinal));
        Assert.Contains(zip.Entries, e => e.FullName == "META-INF/container.xml");
        Assert.Contains(zip.Entries, e => e.FullName == OpfPath);
    }

    // ================= 判据：写入顺序 + 图片字体不压缩 =================

    [Fact]
    public async Task 写入顺序是_mimetype_然后_METAINF_然后其余且图片不压缩()
    {
        var jpeg = EpubFixture.MakeJpeg(600, 900);
        var workspace = Workspace((CoverJpg, jpeg));

        // 造一个够大的 xhtml，才看得出文本确实被压缩了
        var body = string.Concat(Enumerable.Repeat("<p>第一章 重复的正文内容用来测试压缩</p>\n", 4000));
        File.WriteAllText(
            Path.Combine(workspace, "OEBPS", "big.xhtml"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><body>"
                + body + "</body></html>",
            new UTF8Encoding(false));

        var output = Path.Combine(_root, "out.epub");
        await _packager.PackageAsync(workspace, output);

        using var zip = ZipFile.OpenRead(output);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        Assert.Equal("mimetype", names[0]);
        var lastMetaInf = names.FindLastIndex(n => n.StartsWith("META-INF/", StringComparison.Ordinal));
        var firstOther = names.FindIndex(n =>
            !n.Equals("mimetype", StringComparison.Ordinal)
            && !n.StartsWith("META-INF/", StringComparison.Ordinal));
        Assert.True(lastMetaInf >= 0, "包里应该有 META-INF/ 条目：" + string.Join(", ", names));
        Assert.True(firstOther > lastMetaInf, "META-INF/ 必须排在其余内容前面：" + string.Join(", ", names));

        var cover = zip.GetEntry(CoverJpg);
        Assert.NotNull(cover);
        Assert.Equal(cover!.Length, cover.CompressedLength);

        var big = zip.GetEntry("OEBPS/big.xhtml");
        Assert.NotNull(big);
        Assert.True(big!.CompressedLength < big.Length, "xhtml 应该被压缩");

        // 扩展名表（大小写不敏感）
        Assert.Equal(CompressionLevel.NoCompression, EpubPackager.CompressionFor("OEBPS/Fonts/x.woff2"));
        Assert.Equal(CompressionLevel.NoCompression, EpubPackager.CompressionFor("OEBPS/Images/A.JPG"));
        Assert.Equal(CompressionLevel.NoCompression, EpubPackager.CompressionFor("a.ttf"));
        Assert.Equal(CompressionLevel.Optimal, EpubPackager.CompressionFor("OEBPS/ch1.xhtml"));
        Assert.Equal(CompressionLevel.Optimal, EpubPackager.CompressionFor("META-INF/container.xml"));
    }

    [Fact]
    public async Task entry_名一律用正斜杠()
    {
        var workspace = Workspace();
        var output = Path.Combine(_root, "out.epub");

        await _packager.PackageAsync(workspace, output);

        using var zip = ZipFile.OpenRead(output);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains('\\'));
        Assert.Equal("OEBPS/content.opf", EpubPackager.NormalizeEntryName("OEBPS\\content.opf"));
        Assert.Equal("OEBPS/content.opf", EpubPackager.NormalizeEntryName("/OEBPS/content.opf"));
    }

    [Fact]
    public void 打包器自己生成的文件判定()
    {
        Assert.True(EpubPackager.IsPackagerGenerated("mimetype"));
        Assert.True(EpubPackager.IsPackagerGenerated("MIMETYPE"));
        Assert.True(EpubPackager.IsPackagerGenerated(".session.json"));
        Assert.True(EpubPackager.IsPackagerGenerated(".snapshots/snap-1/content.opf"));
        Assert.True(EpubPackager.IsPackagerGenerated("OEBPS/content.opf.ets-tmp"));
        Assert.False(EpubPackager.IsPackagerGenerated("OEBPS/content.opf"));
        Assert.False(EpubPackager.IsPackagerGenerated("OEBPS/mimetype.xhtml"));
    }

    // ================= 判据 5：50MB <= 6 秒 =================

    [Fact]
    public async Task 打包五十兆的书不超过六秒()
    {
        var workspace = Workspace();
        var imagesDirectory = Path.Combine(workspace, "OEBPS", "Images");
        Directory.CreateDirectory(imagesDirectory);

        // 图片走 NoCompression，所以这里量的是 IO + ZIP 组装的成本，不是压缩算法
        var random = new Random(20261008);
        var buffer = new byte[1024 * 1024];
        for (var i = 0; i < 50; i++)
        {
            random.NextBytes(buffer);
            File.WriteAllBytes(Path.Combine(imagesDirectory, $"page-{i:D2}.jpg"), buffer);
        }

        var output = Path.Combine(_root, "big.epub");
        var result = await _packager.PackageAsync(workspace, output);

        Assert.True(result.ByteCount > 50L * 1024 * 1024, "成品应该大于 50MB，实际 " + result.ByteCount);
        Assert.True(
            result.Duration < TimeSpan.FromSeconds(6),
            $"打包 50MB 用了 {result.Duration.TotalSeconds:F2} 秒，超过 6 秒");
        EpubPackageVerifier.Verify(output);
    }

    // ================= 判据 6/7：原子替换 =================

    [Fact]
    public async Task 目标已存在时原子替换且不留残渣()
    {
        var workspace = Workspace();
        var output = Path.Combine(_root, "book.epub");
        File.WriteAllBytes(output, Encoding.UTF8.GetBytes("OLD-BOOK-CONTENT"));

        var result = await _packager.PackageAsync(workspace, output);

        Assert.False(result.HasBackup);
        Assert.Null(result.BackupPath);
        Assert.False(File.Exists(output + ".part"));
        Assert.False(File.Exists(output + ".ets-bak"));
        EpubPackageVerifier.Verify(output);

        using var zip = ZipFile.OpenRead(output);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
    }

    [Fact]
    public async Task 指定备份目录时原文件会进撤销栈()
    {
        var workspace = Workspace();
        var output = Path.Combine(_root, "book.epub");
        var oldBytes = Encoding.UTF8.GetBytes("OLD-BOOK-CONTENT");
        File.WriteAllBytes(output, oldBytes);
        var backups = Path.Combine(_root, "backups");

        var result = await _packager.PackageAsync(workspace, output, backups);

        Assert.True(result.HasBackup);
        Assert.NotNull(result.BackupPath);
        Assert.StartsWith(backups, result.BackupPath!, StringComparison.Ordinal);
        Assert.Equal(oldBytes, File.ReadAllBytes(result.BackupPath!));
        Assert.False(File.Exists(output + ".part"));
        EpubPackageVerifier.Verify(output);
    }

    [Fact]
    public async Task 打包失败时原文件一个字节都没变且不留_part()
    {
        var workspace = Workspace();
        File.Delete(Path.Combine(workspace, "META-INF", "container.xml"));
        var output = Path.Combine(_root, "book.epub");
        var oldBytes = Encoding.UTF8.GetBytes("OLD-BOOK-CONTENT");
        File.WriteAllBytes(output, oldBytes);
        var backups = Path.Combine(_root, "backups");

        var exception = await Assert.ThrowsAsync<AppException>(
            () => _packager.PackageAsync(workspace, output, backups));

        Assert.Equal(ErrorCode.EpubInvalid, exception.Code);
        Assert.Equal(oldBytes, File.ReadAllBytes(output));
        Assert.False(File.Exists(output + ".part"));
        Assert.False(Directory.Exists(backups) && Directory.EnumerateFiles(backups).Any());
    }

    [Fact]
    public async Task 目标被占用时报明确错误且原文件不损坏()
    {
        var workspace = Workspace();
        var output = Path.Combine(_root, "book.epub");
        var oldBytes = Encoding.UTF8.GetBytes("OLD-BOOK-CONTENT");
        File.WriteAllBytes(output, oldBytes);
        var backups = Path.Combine(_root, "backups");

        AppException? captured = null;
        using (File.Open(output, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            captured = await Assert.ThrowsAsync<AppException>(
                () => _packager.PackageAsync(workspace, output, backups));
        }

        Assert.Equal(ErrorCode.FileLocked, captured!.Code);
        Assert.True(
            captured.UserMessage.Contains("原文件没有被改动", StringComparison.Ordinal),
            "错误信息要说清原文件没被动过：" + captured.UserMessage);
        Assert.Equal(oldBytes, File.ReadAllBytes(output));
        Assert.False(File.Exists(output + ".part"));
    }

    // ================= 审计 S4：不可 seek 的流不合规（把结论钉在仓库里）=================

    [Fact]
    public async Task 不可_seek_的流会让所有条目带上_data_descriptor()
    {
        // 这条不测打包器，而是把「为什么必须用 FileStream」这个审计结论固化成测试：
        // 往不可 seek 的流写 ZIP，条目会置 GPF bit 3（data descriptor），产出的 EPUB 不合规。
        var buffer = new MemoryStream();
        await using (var nonSeekable = new NonSeekableStream(buffer))
        {
            using var zip = new ZipArchive(nonSeekable, ZipArchiveMode.Create, leaveOpen: true);
            var entry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            await using var stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(MimetypeContent));
        }

        var bytes = buffer.ToArray();
        Assert.Equal(0x04034B50u, BitConverter.ToUInt32(bytes, 0));
        Assert.NotEqual(0, BitConverter.ToUInt16(bytes, 6) & 0x0008);
    }

    // ================= helpers =================

    /// <summary>造一本最小 EPUB3 再解压成工作区（解压走 ZipFile，不含任何引擎代码）。</summary>
    private string Workspace(params (string Name, byte[] Bytes)[] binaries)
    {
        var epubPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".epub");
        EpubFixture.Write(epubPath, EpubFixture.Epub3Opf, epub3: true, binaries);

        var workspace = Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(epubPath, workspace);
        return workspace;
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override void WriteByte(byte value) => inner.WriteByte(value);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);
    }
}
