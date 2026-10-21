using System.IO.Compression;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Engines.Comics;

namespace EbookToolSuite.Engines.Tests.Comics;

/// <summary>
/// 漫画归档读取（T6-3）：自然序、垃圾条目、按页读、明确报错。
/// 只验证"条目与字节"，不验证解码——解码在 Services 的 ComicBookService（另一组测试）。
/// </summary>
public sealed class ComicArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ets-comic-" + Guid.NewGuid().ToString("N")[..8]);

    public ComicArchiveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录清不掉不影响判定：%TEMP% 下系统会自己收。
        }
    }

    private string BuildZip(string fileName, params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_root, fileName);

        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);

                using var entryStream = entry.Open();
                entryStream.Write(bytes);
            }
        }

        return path;
    }

    private static byte[] Bytes(int count, byte filler = 7) => Enumerable.Repeat(filler, count).ToArray();

    [Fact]
    public void Open_图片条目按自然序排列_并忽略垃圾条目()
    {
        var path = BuildZip(
            "pages.cbz",
            ("page10.jpg", Bytes(10)),
            ("page2.jpg", Bytes(2)),
            ("page1.jpg", Bytes(1)),
            ("notes.txt", Bytes(3)),
            ("__MACOSX/page1.jpg", Bytes(4)),
            (".hidden.png", Bytes(5)));

        using var archive = ComicArchive.Open(path);

        Assert.Equal(3, archive.PageCount);
        Assert.Equal(new[] { "page1.jpg", "page2.jpg", "page10.jpg" }, archive.PageKeys);
        Assert.False(archive.IsRar);
        Assert.False(archive.IsEncrypted);
    }

    [Fact]
    public void ReadPage_返回该条目的原始字节()
    {
        var path = BuildZip("read.cbz", ("1.jpg", Bytes(33, 0xAB)), ("2.jpg", Bytes(44, 0xCD)));

        using var archive = ComicArchive.Open(path);

        Assert.Equal(Bytes(33, 0xAB), archive.ReadPage(0));
        Assert.Equal(Bytes(44, 0xCD), archive.ReadPage(1));
    }

    [Fact]
    public void Open_不是压缩包_抛CorruptFile且提示明确()
    {
        var path = Path.Combine(_root, "fake.cbz");
        File.WriteAllText(path, "这不是一个压缩包");

        var ex = Assert.Throws<AppException>(() => ComicArchive.Open(path));

        // 计划 T6-3 的坑：遇到不支持的要明确提示，不要静默失败。
        Assert.Equal(ErrorCode.CorruptFile, ex.Code);
        Assert.Contains("不支持的算法", ex.UserMessage);
    }

    [Fact]
    public void Open_压缩包里没有图片_抛UnsupportedFormat()
    {
        var path = BuildZip("text-only.cbz", ("readme.txt", Bytes(12)));

        var ex = Assert.Throws<AppException>(() => ComicArchive.Open(path));

        Assert.Equal(ErrorCode.UnsupportedFormat, ex.Code);
    }

    [Fact]
    public void Open_有RAR文件头但没有条目_按没有图片处理()
    {
        var path = Path.Combine(_root, "empty.cbr");

        /* RAR5 签名 + 8 字节。SharpCompress 实测把"只有头、没有条目"当空归档（不抛异常），
           所以这里落在"压缩包里没有图片"，而不是"文件损坏"。 */
        File.WriteAllBytes(path, new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 });

        var ex = Assert.Throws<AppException>(() => ComicArchive.Open(path));

        Assert.Equal(ErrorCode.UnsupportedFormat, ex.Code);
    }

    [Fact]
    public void Open_是ZIP文件头但结构坏了_抛CorruptFile且提示明确()
    {
        var path = Path.Combine(_root, "corrupt.cbz");

        // "PK\x03\x04" 后面跟一堆垃圾：认得出是 zip，但读不出中央目录。
        var bytes = new byte[80];
        bytes[0] = 0x50;
        bytes[1] = 0x4B;
        bytes[2] = 0x03;
        bytes[3] = 0x04;
        Random.Shared.NextBytes(bytes.AsSpan(4));

        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<AppException>(() => ComicArchive.Open(path));

        // 计划 T6-3 的坑：遇到不支持的要明确提示，不要静默失败。
        Assert.Equal(ErrorCode.CorruptFile, ex.Code);
        Assert.Contains("不支持的算法", ex.UserMessage);
    }

    [Fact]
    public void Dispose之后读页_抛ObjectDisposedException()
    {
        var path = BuildZip("dispose.cbz", ("1.jpg", Bytes(8)));
        var archive = ComicArchive.Open(path);

        archive.Dispose();

        Assert.Throws<ObjectDisposedException>(() => archive.ReadPage(0));
    }
}
