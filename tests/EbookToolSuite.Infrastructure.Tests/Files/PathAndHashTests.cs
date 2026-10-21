using System.Text;
using EbookToolSuite.Infrastructure.Files;

namespace EbookToolSuite.Infrastructure.Tests.Files;

public sealed class PathUtilTests
{
    [Theory]
    [InlineData("正常书名", "正常书名")]
    [InlineData("a:b*c?d", "a_b_c_d")]
    [InlineData("斜杠/反斜杠\\", "斜杠_反斜杠_")]
    [InlineData("  前后空白  ", "前后空白")]
    [InlineData("结尾有点...", "结尾有点")]
    public void SanitizeFileName_CleansIllegalInput(string input, string expected)
        => Assert.Equal(expected, PathUtil.SanitizeFileName(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void SanitizeFileName_EmptyInput_UsesFallback(string? input)
        => Assert.Equal("untitled", PathUtil.SanitizeFileName(input));

    [Fact]
    public void SanitizeFileName_CustomFallback_IsUsed()
        => Assert.Equal("abc123", PathUtil.SanitizeFileName(null, "abc123"));

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void SanitizeFileName_ReservedDeviceNames_ArePrefixed(string reserved)
    {
        var result = PathUtil.SanitizeFileName(reserved);

        Assert.NotEqual(reserved, result);
        Assert.StartsWith("_", result);
    }

    [Fact]
    public void SanitizeFileName_ReservedNameWithExtension_IsPrefixed()
        => Assert.StartsWith("_", PathUtil.SanitizeFileName("CON.txt"));

    [Fact]
    public void SanitizeFileName_LongName_IsTruncated()
    {
        var result = PathUtil.SanitizeFileName(new string('长', 500));

        Assert.True(result.Length <= 120, "实际长度 " + result.Length);
    }

    [Fact]
    public void BuildLibraryPath_UsesPerBookFolderAndKeepsStem()
    {
        var bookId = Guid.NewGuid();
        var path = PathUtil.BuildLibraryPath(@"C:\Data\Library", bookId, "Sample Book K.epub");

        Assert.Equal(Path.Combine(@"C:\Data\Library", bookId.ToString("D"), "Sample Book K.epub"), path);
    }

    [Fact]
    public void BuildLibraryPath_LowercasesExtensionAndDropsOldOne()
    {
        var path = PathUtil.BuildLibraryPath(@"C:\Data\Library", Guid.NewGuid(), "Book.EPUB");
        Assert.EndsWith("Book.epub", path);
    }

    [Fact]
    public void BuildLibraryPath_NullOriginalName_StillProducesUsablePath()
    {
        var bookId = Guid.NewGuid();
        var path = PathUtil.BuildLibraryPath(@"C:\Data\Library", bookId, null, ".epub");

        Assert.True(Path.IsPathFullyQualified(path));
        Assert.EndsWith(".epub", path);
        Assert.Contains(bookId.ToString("D"), path);
    }

    [Fact]
    public void BuildLibraryPath_SameBookDifferentFormats_DoNotCollide()
    {
        var bookId = Guid.NewGuid();
        var epub = PathUtil.BuildLibraryPath(@"C:\L", bookId, "书.epub");
        var mobi = PathUtil.BuildLibraryPath(@"C:\L", bookId, "书.mobi");

        Assert.NotEqual(epub, mobi);
    }

    [Fact]
    public void PartPath_AppendsSuffixInSameFolder()
    {
        var part = PathUtil.PartPath(@"C:\Data\Library\a\b.epub");

        Assert.Equal(@"C:\Data\Library\a\b.epub.part", part);
        Assert.Equal(Path.GetDirectoryName(@"C:\Data\Library\a\b.epub"), Path.GetDirectoryName(part));
    }
}

public sealed class HashUtilTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ets-hash-" + Guid.NewGuid().ToString("N"));

    private readonly HashUtil _hash = new();

    public HashUtilTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task QuickFingerprint_IsStableForSameContent()
    {
        var a = Write("a.bin", 100_000);
        var b = Write("b.bin", 100_000);

        Assert.Equal(
            await _hash.QuickFingerprintAsync(a),
            await _hash.QuickFingerprintAsync(b));
    }

    [Fact]
    public async Task QuickFingerprint_DiffersWhenTailChanges()
    {
        var a = Write("a.bin", 200_000, seed: 1);
        var b = Write("b.bin", 200_000, seed: 2);

        Assert.NotEqual(
            await _hash.QuickFingerprintAsync(a),
            await _hash.QuickFingerprintAsync(b));
    }

    [Fact]
    public async Task QuickFingerprint_IncludesLength()
    {
        // 同样的头尾字节，只是更长：必须得到不同的指纹
        var shortFile = Path.Combine(_dir, "short.bin");
        var longFile = Path.Combine(_dir, "long.bin");
        await File.WriteAllBytesAsync(shortFile, Enumerable.Repeat((byte)'A', 300_000).ToArray());
        await File.WriteAllBytesAsync(longFile, Enumerable.Repeat((byte)'A', 400_000).ToArray());

        Assert.NotEqual(
            await _hash.QuickFingerprintAsync(shortFile),
            await _hash.QuickFingerprintAsync(longFile));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(64 * 1024)]
    [InlineData(64 * 1024 + 1)]
    public async Task QuickFingerprint_HandlesEdgeSizes(int size)
    {
        var path = Write("edge.bin", size);

        var fingerprint = await _hash.QuickFingerprintAsync(path);

        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, c => Assert.Contains(c, "0123456789ABCDEF"));
    }

    [Fact]
    public async Task QuickFingerprint_MissingFile_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _hash.QuickFingerprintAsync(Path.Combine(_dir, "nope.bin")));
    }

    private string Write(string name, int size, int? seed = null)
    {
        var path = Path.Combine(_dir, name);
        var bytes = new byte[size];

        if (seed is null)
        {
            Array.Fill(bytes, (byte)'x');
        }
        else
        {
            Random.Shared.NextBytes(bytes);
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }
    [Fact]
    public async Task ContentHash_MatchesKnownSha256Vectors()
    {
        var abc = Path.Combine(_dir, "abc.bin");
        await File.WriteAllBytesAsync(abc, Encoding.ASCII.GetBytes("abc"));
        Assert.Equal(
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
            await _hash.ComputeContentHashAsync(abc));

        var empty = Path.Combine(_dir, "empty.bin");
        await File.WriteAllBytesAsync(empty, Array.Empty<byte>());
        Assert.Equal(
            "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
            await _hash.ComputeContentHashAsync(empty));
    }

    [Fact]
    public async Task ContentHash_DistinguishesWhatQuickFingerprintCollidesOn()
    {
        // 头 64KB 与尾 64KB 完全相同、长度也相同，只有中间 4KB 不同：
        // 快速指纹必然相同，全文件哈希必须不同。这就是 T2-2 要复核的碰撞场景。
        var a = Path.Combine(_dir, "mid-a.bin");
        var b = Path.Combine(_dir, "mid-b.bin");

        WriteSameHeadAndTail(a, 0x01);
        WriteSameHeadAndTail(b, 0x02);

        Assert.Equal(
            await _hash.QuickFingerprintAsync(a),
            await _hash.QuickFingerprintAsync(b));

        Assert.NotEqual(
            await _hash.ComputeContentHashAsync(a),
            await _hash.ComputeContentHashAsync(b));
    }

    [Fact]
    public async Task ContentHash_IsStableAndUppercaseHex()
    {
        var a = Write("h1.bin", 5000);
        var b = Write("h2.bin", 5000);

        var first = await _hash.ComputeContentHashAsync(a);

        Assert.Equal(64, first.Length);
        Assert.All(first, c => Assert.Contains(c, "0123456789ABCDEF"));
        Assert.Equal(first, await _hash.ComputeContentHashAsync(a));
        Assert.Equal(first, await _hash.ComputeContentHashAsync(b));
    }

    [Fact]
    public async Task ContentHash_MissingFile_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _hash.ComputeContentHashAsync(Path.Combine(_dir, "nope.bin")));
    }

    /// <summary>
    /// 造一个头 64KB 与尾 64KB 相同、仅中间 4KB 不同的文件（长度固定 135168）。
    /// </summary>
    private static void WriteSameHeadAndTail(string path, byte midByte)
    {
        const int window = 64 * 1024;
        var bytes = new byte[(2 * window) + 4096];
        Array.Fill(bytes, (byte)'A');
        for (var i = window; i < window + 4096; i++)
        {
            bytes[i] = midByte;
        }

        File.WriteAllBytes(path, bytes);
    }
}
