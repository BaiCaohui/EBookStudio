using EbookToolSuite.Services.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests;

/// <summary>
/// 扫描器是纯路径操作，用真实的临时目录树比 mock 文件系统更可信
/// （T2-1 的判据就是"递归扫描一个含 200 个文件的目录"）。
/// </summary>
public sealed class ImportScannerTests : IDisposable
{
    private readonly string _root;

    public ImportScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ets-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

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

    private static ImportScanner NewScanner() => new(NullLogger<ImportScanner>.Instance);

    private string Touch(string relative)
    {
        var full = Path.Combine(_root, relative);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(full, new byte[] { 1, 2, 3 });
        return full;
    }

    [Fact]
    public void Scan_单个文件_原样返回()
    {
        var file = Touch("a.epub");

        var result = NewScanner().Scan(new[] { file }, recursive: true);

        Assert.Single(result.Files);
        Assert.Equal(file, result.Files[0]);
        Assert.Equal(0, result.SkippedUnsupported);
        Assert.Empty(result.MissingPaths);
        Assert.False(result.Canceled);
    }

    [Fact]
    public void Scan_文件夹递归_找齐嵌套文件并统计非电子书()
    {
        Touch("a.epub");
        Touch("sub\\b.mobi");
        Touch("sub\\deep\\c.pdf");
        Touch("sub\\deep\\d.jpg");
        Touch("note.docx");
        Touch("meta.opf");

        var result = NewScanner().Scan(new[] { _root }, recursive: true);

        Assert.Equal(3, result.Count);
        Assert.Equal(3, result.SkippedUnsupported);
        Assert.Equal(3, result.FoldersScanned);
        Assert.All(result.Files, f => Assert.StartsWith(_root, f, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scan_不递归_只收顶层文件()
    {
        Touch("a.epub");
        Touch("sub\\b.mobi");

        var result = NewScanner().Scan(new[] { _root }, recursive: false);

        Assert.Single(result.Files);
        Assert.EndsWith("a.epub", result.Files[0]);
        Assert.Equal(1, result.FoldersScanned);
    }

    [Fact]
    public void Scan_同一个文件选两次_只算一次()
    {
        var file = Touch("a.epub");

        var result = NewScanner().Scan(new[] { file, file, _root }, recursive: true);

        Assert.Single(result.Files);
    }

    [Fact]
    public void Scan_路径不存在_记进MissingPaths不抛异常()
    {
        var ghost = Path.Combine(_root, "没有这个文件夹");
        var file = Touch("a.epub");

        var result = NewScanner().Scan(new[] { ghost, file }, recursive: true);

        Assert.Single(result.Files);
        Assert.Single(result.MissingPaths);
        Assert.Equal(ghost, result.MissingPaths[0]);
    }

    [Fact]
    public void Scan_顺序稳定_按路径排序()
    {
        Touch("c.epub");
        Touch("a.epub");
        Touch("b.epub");

        var first = NewScanner().Scan(new[] { _root }, recursive: true).Files;
        var second = NewScanner().Scan(new[] { _root }, recursive: true).Files;

        Assert.Equal(new[] { "a.epub", "b.epub", "c.epub" },
            first.Select(Path.GetFileName).ToArray());
        Assert.Equal(first, second);
    }

    [Fact]
    public void Scan_空列表_返回空结果不抛异常()
    {
        var result = NewScanner().Scan(Array.Empty<string>(), recursive: true);

        Assert.Equal(0, result.Count);
        Assert.Contains("找到 0 个文件", result.Summary);
    }

    [Fact]
    public void Scan_已取消_抛OperationCanceledException()
    {
        Touch("a.epub");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => NewScanner().Scan(new[] { _root }, recursive: true, cts.Token));
    }
}
