using System.IO.Compression;
using System.Text;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Comics;

namespace EbookToolSuite.Engines.Tests.Comics;

/// <summary>
/// 漫画只读探测（T6-3）：导入阶段只回答「是不是漫画包、多少页」，且**永不抛异常**。
/// </summary>
public sealed class ComicProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ets-comicprobe-" + Guid.NewGuid().ToString("N")[..8]);

    public ComicProbeTests() => Directory.CreateDirectory(_root);

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
            // 见 ComicArchiveTests：临时目录清不掉不影响判定。
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

    private static byte[] ComicInfoBytes(ComicMetadata metadata, int pageCount)
    {
        using var stream = new MemoryStream();
        ComicInfoXml.Write(stream, metadata, pageCount);
        return stream.ToArray();
    }

    [Fact]
    public void Inspect_有效CBZ_报告页数与格式()
    {
        var path = BuildZip(
            "good.cbz",
            ("page1.jpg", new byte[16]),
            ("page2.jpg", new byte[16]),
            ("page10.jpg", new byte[16]));

        var result = ComicProbe.Inspect(path);

        Assert.True(result.LooksLikeComic);
        Assert.Equal(3, result.PageCount);
        Assert.False(result.IsRar);
        Assert.False(result.IsEncrypted);
        Assert.Null(result.Error);
        Assert.True(result.FileSize > 0);
        // 没有 ComicInfo.xml：元数据为 null，不编造书籍信息。
        Assert.Null(result.Metadata);
    }

    [Fact]
    public void Inspect_读回包内ComicInfo元数据()
    {
        var metadata = new ComicMetadata
        {
            Title = "漫画标题",
            Writer = "编剧",
            Penciller = "画师",
            Publisher = "出版社",
            Summary = "简介",
            LanguageISO = "zh",
            Series = "系列名",
            Number = "3",
            Year = 2023,
            Month = 5,
            Day = 20,
            Isbn = "978-7",
        };

        var path = BuildZip(
            "with-info.cbz",
            (ComicInfoXml.EntryName, ComicInfoBytes(metadata, 2)),
            ("1.jpg", new byte[16]),
            ("2.jpg", new byte[16]));

        var result = ComicProbe.Inspect(path);

        Assert.True(result.LooksLikeComic);
        Assert.Equal(2, result.PageCount);
        Assert.NotNull(result.Metadata);
        Assert.Equal("漫画标题", result.Metadata!.Title);
        Assert.Equal("编剧", result.Metadata.Writer);
        Assert.Equal("出版社", result.Metadata.Publisher);
        Assert.Equal("系列名", result.Metadata.Series);
        Assert.Equal(2023, result.Metadata.Year);
        Assert.Equal(5, result.Metadata.Month);
        Assert.Equal(20, result.Metadata.Day);
        Assert.Equal(2, result.Metadata.PageCount);
    }

    [Fact]
    public void Inspect_ComicInfo条目名大小写不敏感()
    {
        var path = BuildZip(
            "upper.cbz",
            ("COMICINFO.XML", ComicInfoBytes(new ComicMetadata { Title = "大写文件名" }, 1)),
            ("page.jpg", new byte[16]));

        var result = ComicProbe.Inspect(path);

        Assert.True(result.LooksLikeComic);
        Assert.Equal("大写文件名", result.Metadata?.Title);
    }

    [Fact]
    public void Inspect_ComicInfo损坏_不影响页数与漫画判定()
    {
        var path = BuildZip(
            "broken-info.cbz",
            (ComicInfoXml.EntryName, Encoding.UTF8.GetBytes("<ComicInfo><Title>没闭合")),
            ("page.jpg", new byte[16]));

        var result = ComicProbe.Inspect(path);

        Assert.True(result.LooksLikeComic);
        Assert.Equal(1, result.PageCount);
        Assert.Null(result.Metadata);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Inspect_普通文本文件_不是漫画包()
    {
        var path = Path.Combine(_root, "note.cbz");
        File.WriteAllText(path, "只是改了个扩展名的文本");

        var result = ComicProbe.Inspect(path);

        Assert.False(result.LooksLikeComic);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Inspect_合法zip但没有图片_不是漫画包()
    {
        var path = BuildZip("docs.cbz", ("a.txt", new byte[8]), ("b.html", new byte[8]));

        var result = ComicProbe.Inspect(path);

        Assert.False(result.LooksLikeComic);
    }

    [Fact]
    public void Inspect_有RAR头但没有条目_不是漫画包()
    {
        var path = Path.Combine(_root, "empty.cbr");
        File.WriteAllBytes(path, new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x00 });

        var result = ComicProbe.Inspect(path);

        // SharpCompress 把"只有头、没有条目"当空归档：结果是"没有图片"，不是"坏包"。
        Assert.False(result.LooksLikeComic);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Inspect_结构损坏的zip_像漫画但如实带错误()
    {
        var path = Path.Combine(_root, "corrupt.cbz");

        var bytes = new byte[80];
        bytes[0] = 0x50;
        bytes[1] = 0x4B;
        bytes[2] = 0x03;
        bytes[3] = 0x04;
        Random.Shared.NextBytes(bytes.AsSpan(4));
        File.WriteAllBytes(path, bytes);

        var result = ComicProbe.Inspect(path);

        // 认得出是压缩包、但打不开：必须带原因（导入时会明确显示给用户）。
        Assert.True(result.LooksLikeComic);
        Assert.NotNull(result.Error);
        Assert.Contains("不支持的算法", result.Error!);
    }

    [Fact]
    public void Inspect_文件不存在_不抛异常()
    {
        var result = ComicProbe.Inspect(Path.Combine(_root, "missing.cbz"));

        Assert.False(result.LooksLikeComic);
        Assert.Equal(0, result.FileSize);
    }
}
