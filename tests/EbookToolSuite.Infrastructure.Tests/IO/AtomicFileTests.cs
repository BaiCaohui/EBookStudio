using System.Text;
using EbookToolSuite.Core.IO;

namespace EbookToolSuite.Infrastructure.Tests.IO;

/// <summary>T7-2 判据 4：原子写（临时文件 + 原子替换）。</summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ets-atomic-" + Guid.NewGuid().ToString("N")[..8]);

    public AtomicFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public void 写文本后内容正确且不残留临时文件()
    {
        var target = Path.Combine(_root, "a.txt");

        AtomicFile.WriteAllText(target, "你好，原子写", new UTF8Encoding(false));

        Assert.Equal("你好，原子写", File.ReadAllText(target, Encoding.UTF8));
        Assert.False(File.Exists(target + AtomicFile.TempSuffix));
    }

    [Fact]
    public void 覆盖已有文件且旧内容不残留()
    {
        var target = Path.Combine(_root, "b.txt");
        File.WriteAllText(target, "旧内容");

        AtomicFile.WriteAllText(target, "新内容", new UTF8Encoding(false));

        Assert.Equal("新内容", File.ReadAllText(target));
        Assert.False(File.Exists(target + AtomicFile.TempSuffix));
    }

    [Fact]
    public void 写字节后逐字节一致()
    {
        var target = Path.Combine(_root, "c.bin");
        var bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();

        AtomicFile.WriteAllBytes(target, bytes);

        Assert.Equal(bytes, File.ReadAllBytes(target));
    }

    [Fact]
    public void 目录不存在时失败且不创建目标与临时文件()
    {
        var target = Path.Combine(_root, "no-such-dir", "d.txt");

        Assert.Throws<DirectoryNotFoundException>(
            () => AtomicFile.WriteAllText(target, "x", Encoding.UTF8));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + AtomicFile.TempSuffix));
    }

    [Fact]
    public void 流式写入回调可用()
    {
        var target = Path.Combine(_root, "e.txt");

        AtomicFile.Write(target, stream =>
        {
            var bytes = Encoding.UTF8.GetBytes("通过流写入");
            stream.Write(bytes, 0, bytes.Length);
        });

        Assert.Equal("通过流写入", File.ReadAllText(target));
    }

    [Fact]
    public void 复制文件后内容一致且不残留临时文件()
    {
        var source = Path.Combine(_root, "src.txt");
        var target = Path.Combine(_root, "dst.txt");
        File.WriteAllText(source, "源文件");

        AtomicFile.Copy(source, target);

        Assert.Equal("源文件", File.ReadAllText(target));
        Assert.False(File.Exists(target + AtomicFile.TempSuffix));
    }

    [Fact]
    public void 复制时源文件不存在则失败且不留下目标()
    {
        var source = Path.Combine(_root, "missing.txt");
        var target = Path.Combine(_root, "dst2.txt");

        Assert.Throws<FileNotFoundException>(() => AtomicFile.Copy(source, target));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + AtomicFile.TempSuffix));
    }
}
