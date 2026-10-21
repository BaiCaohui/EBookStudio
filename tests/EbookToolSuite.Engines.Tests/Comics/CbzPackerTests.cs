using System.IO.Compression;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Comics;

namespace EbookToolSuite.Engines.Tests.Comics;

/// <summary>CBZ 打包：白名单、自然序、跳过策略、原子落盘、取消不留残渣。</summary>
public sealed class CbzPackerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ets-cbz-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly CbzPacker _packer = new();

    public CbzPackerTests() => Directory.CreateDirectory(_root);

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
            // 清理失败不该让测试红：临时目录在 %TEMP% 下，系统会自己收。
        }
    }

    /// <summary>造一张「假图片」：内容不重要，打包只按扩展名认。</summary>
    private string AddFile(string folder, string name, int bytes = 64)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private string NewFolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void ScanImages_只认白名单且按自然序返回()
    {
        var folder = NewFolder("scan");
        AddFile(folder, "10.jpg");
        AddFile(folder, "2.jpg");
        AddFile(folder, "1.jpg");
        AddFile(folder, "note.txt");
        AddFile(folder, "cover.PNG");

        var images = _packer.ScanImages(folder);

        Assert.Equal(4, images.Count);
        Assert.Equal("1.jpg", Path.GetFileName(images[0]));
        Assert.Equal("2.jpg", Path.GetFileName(images[1]));
        Assert.Equal("10.jpg", Path.GetFileName(images[2]));
        Assert.Equal("cover.PNG", Path.GetFileName(images[3]));
    }

    [Fact]
    public void ScanImages_默认不进子目录_递归时进()
    {
        var folder = NewFolder("deep");
        AddFile(folder, "1.jpg");
        AddFile(Path.Combine(folder, "sub"), "2.jpg");

        Assert.Single(_packer.ScanImages(folder));
        Assert.Equal(2, _packer.ScanImages(folder, recursive: true).Count);
    }

    [Fact]
    public void ScanImages_文件夹不存在时返回空_不抛()
    {
        Assert.Empty(_packer.ScanImages(Path.Combine(_root, "根本没有这个目录")));
    }

    [Fact]
    public void IsSupportedImage_认扩展名不认内容()
    {
        Assert.True(CbzPacker.IsSupportedImage("a.JPG"));
        Assert.True(CbzPacker.IsSupportedImage("a.webp"));
        Assert.False(CbzPacker.IsSupportedImage("a.txt"));
        Assert.False(CbzPacker.IsSupportedImage("a.tiff"));
    }

    [Fact]
    public async Task PackAsync_产物能读回_条目保留原名且按自然序()
    {
        var folder = NewFolder("pack");
        AddFile(folder, "10.jpg");
        AddFile(folder, "2.jpg");
        AddFile(folder, "1.jpg");
        AddFile(folder, "note.txt");

        var output = Path.Combine(_root, "out.cbz");
        var result = await _packer.PackAsync(folder, output);

        Assert.Equal(3, result.ImageCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(output, result.OutputPath);
        Assert.Equal(new FileInfo(output).Length, result.SizeBytes);

        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(3, zip.Entries.Count);
        Assert.Equal("1.jpg", zip.Entries[0].FullName);
        Assert.Equal("2.jpg", zip.Entries[1].FullName);
        Assert.Equal("10.jpg", zip.Entries[2].FullName);
    }

    [Fact]
    public async Task PackAsync_空文件夹抛CbzPackFailed()
    {
        var folder = NewFolder("empty");
        AddFile(folder, "readme.txt");

        var ex = await Assert.ThrowsAsync<AppException>(
            () => _packer.PackAsync(folder, Path.Combine(_root, "x.cbz")));

        Assert.Equal(ErrorCode.CbzPackFailed, ex.Code);
        Assert.False(File.Exists(Path.Combine(_root, "x.cbz")));
    }

    [Fact]
    public async Task PackAsync_文件夹不存在抛CbzPackFailed()
    {
        var ex = await Assert.ThrowsAsync<AppException>(
            () => _packer.PackAsync(Path.Combine(_root, "没有这个目录"), Path.Combine(_root, "y.cbz")));

        Assert.Equal(ErrorCode.CbzPackFailed, ex.Code);
    }

    [Fact]
    public async Task PackAsync_取消时不产出文件也不留临时文件()
    {
        var folder = NewFolder("cancel");
        for (var i = 1; i <= 5; i++)
        {
            AddFile(folder, i + ".jpg");
        }

        var output = Path.Combine(_root, "cancel.cbz");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _packer.PackAsync(folder, output, null, cts.Token));

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_root, "*.part-*"));
    }

    [Fact]
    public async Task PackAsync_覆盖已存在的产物()
    {
        var folder = NewFolder("overwrite");
        AddFile(folder, "1.jpg");

        var output = Path.Combine(_root, "same.cbz");
        await File.WriteAllTextAsync(output, "旧内容");

        var result = await _packer.PackAsync(folder, output);

        Assert.Equal(1, result.ImageCount);
        using var zip = ZipFile.OpenRead(output);
        Assert.Single(zip.Entries);
    }

    [Fact]
    public async Task PackAsync_进度回调走到百分之百()
    {
        var folder = NewFolder("progress");
        AddFile(folder, "1.jpg");
        AddFile(folder, "2.jpg");

        var seen = new List<double?>();
        var progress = new CollectingProgress(seen);

        await _packer.PackAsync(folder, Path.Combine(_root, "p.cbz"), progress);

        Assert.Equal(2, seen.Count);
        Assert.Equal(1d, seen[^1]);
    }

    [Fact]
    public async Task PackAsync_按指定顺序打包_ComicInfo是第一个条目()
    {
        var folder = NewFolder("ordered");
        var cover = AddFile(folder, "5.jpg");
        var middle = AddFile(folder, "1.jpg");
        var last = AddFile(folder, "9.jpg");

        // 故意与文件名自然序相反：打包必须信列表顺序，而不是自己再排一遍。
        var entries = new List<CbzPackEntry>
        {
            new(last, "0003_9.jpg"),
            new(cover, "0001_5.jpg"),
            new(middle, "0002_1.jpg"),
        };

        var metadata = new ComicMetadata
        {
            Title = "夏日漫画",
            Writer = "作者甲",
            Number = "2",
            Year = 2024,
        };

        var output = Path.Combine(_root, "ordered.cbz");
        var result = await _packer.PackAsync(entries, metadata, output);

        Assert.Equal(3, result.ImageCount);
        Assert.Equal(0, result.SkippedCount);

        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(4, zip.Entries.Count);
        Assert.Equal(ComicInfoXml.EntryName, zip.Entries[0].FullName);
        Assert.Equal(
            new[] { "0003_9.jpg", "0001_5.jpg", "0002_1.jpg" },
            zip.Entries.Skip(1).Select(entry => entry.FullName));

        using var reader = new StreamReader(zip.Entries[0].Open());
        Assert.True(ComicInfoXml.TryParse(reader.ReadToEnd(), out var parsed));
        Assert.Equal("夏日漫画", parsed.Title);
        Assert.Equal(3, parsed.PageCount);
    }

    [Fact]
    public async Task PackAsync_没有用户书籍信息时不写ComicInfo()
    {
        var folder = NewFolder("no-meta");
        var path = AddFile(folder, "1.jpg");
        var entries = new List<CbzPackEntry> { new(path, "1.jpg") };

        // null 与「什么都没填的空对象」等价：包内不该多一个只有页数的空元数据。
        foreach (var metadata in new ComicMetadata?[] { null, new ComicMetadata() })
        {
            var output = Path.Combine(_root, "no-meta-" + Guid.NewGuid().ToString("N")[..6] + ".cbz");
            var result = await _packer.PackAsync(entries, metadata, output);

            Assert.Equal(1, result.ImageCount);

            using var zip = ZipFile.OpenRead(output);
            Assert.Single(zip.Entries);
            Assert.DoesNotContain(zip.Entries, entry => entry.FullName == ComicInfoXml.EntryName);
        }
    }

    [Fact]
    public async Task PackAsync_条目重名忽略大小写_抛CbzPackFailed()
    {
        var folder = NewFolder("duplicate");
        var first = AddFile(folder, "a.jpg");
        var second = AddFile(folder, "b.jpg");

        var entries = new List<CbzPackEntry>
        {
            new(first, "page.jpg"),
            new(second, "PAGE.JPG"),
        };

        var output = Path.Combine(_root, "duplicate.cbz");

        var ex = await Assert.ThrowsAsync<AppException>(
            () => _packer.PackAsync(entries, null, output));

        Assert.Equal(ErrorCode.CbzPackFailed, ex.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task PackAsync_条目名含斜杠_抛CbzPackFailed()
    {
        var folder = NewFolder("bad-name");
        var path = AddFile(folder, "1.jpg");
        var entries = new List<CbzPackEntry> { new(path, "sub/1.jpg") };

        var output = Path.Combine(_root, "bad-name.cbz");

        var ex = await Assert.ThrowsAsync<AppException>(
            () => _packer.PackAsync(entries, null, output));

        Assert.Equal(ErrorCode.CbzPackFailed, ex.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task PackAsync_读不出的源文件跳过并计数_ComicInfo页数按实际写入数()
    {
        var folder = NewFolder("skip-one");
        var good = AddFile(folder, "good.jpg");
        var missing = Path.Combine(folder, "missing.jpg");

        var entries = new List<CbzPackEntry>
        {
            new(missing, "0001_missing.jpg"),
            new(good, "0002_good.jpg"),
        };

        var output = Path.Combine(_root, "skip-one.cbz");
        var result = await _packer.PackAsync(entries, new ComicMetadata { Title = "跳过测试" }, output);

        Assert.Equal(1, result.ImageCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal("0001_missing.jpg", Assert.Single(result.Skipped));

        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(2, zip.Entries.Count);
        Assert.Equal(ComicInfoXml.EntryName, zip.Entries[0].FullName);
        Assert.Equal("0002_good.jpg", zip.Entries[1].FullName);

        // 坏文件被跳过之后，PageCount 写的是真正进包的页数，不是调用方列表长度。
        using var reader = new StreamReader(zip.Entries[0].Open());
        Assert.True(ComicInfoXml.TryParse(reader.ReadToEnd(), out var parsed));
        Assert.Equal(1, parsed.PageCount);
    }

    [Fact]
    public async Task PackAsync_空条目列表抛CbzPackFailed()
    {
        var ex = await Assert.ThrowsAsync<AppException>(
            () => _packer.PackAsync(
                Array.Empty<CbzPackEntry>(), null, Path.Combine(_root, "empty-entries.cbz")));

        Assert.Equal(ErrorCode.CbzPackFailed, ex.Code);
    }

    private sealed class CollectingProgress : IProgress<Core.Models.ConversionProgress>
    {
        private readonly List<double?> _sink;

        public CollectingProgress(List<double?> sink) => _sink = sink;

        public void Report(Core.Models.ConversionProgress value) => _sink.Add(value.Percent);
    }
}
