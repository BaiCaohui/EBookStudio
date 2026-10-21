using System.IO.Compression;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Services.Tests;

public sealed class EpubProbeTests : IDisposable
{
    private readonly string _dir;
    private readonly EpubProbe _probe = new();

    public EpubProbeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ets-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Probe_DeclaredCover_IsUsedWithoutFallback()
    {
        var path = Save(new EpubBuilder { CoverBytes = EpubBuilder.MakeJpeg(80, 80) });

        var probe = await _probe.ProbeAsync(path, loadCover: true);

        Assert.True(probe.HasCover);
        Assert.False(probe.CoverFromFallback);
    }

    [Fact]
    public async Task Probe_UndeclaredCover_FallsBackToLargestImage()
    {
        // 小图在前、大图在后：兜底策略应该挑大的那本
        var small = EpubBuilder.MakeJpeg(60, 60);
        var big = EpubBuilder.MakeJpeg(1200, 1600);
        var builder = new EpubBuilder
        {
            CoverBytes = small,
            ExtraImageBytes = big,
            DeclareCover = false,
        };
        var path = builder.WriteTo(Path.Combine(_dir, "fallback.epub"));

        var probe = await _probe.ProbeAsync(path, loadCover: true);

        Assert.True(probe.HasCover);
        Assert.True(probe.CoverFromFallback);
        Assert.Equal(big.Length, probe.CoverBytes!.Length);
    }

    [Fact]
    public async Task Probe_NoImages_HasNoCover()
    {
        var path = Save(new EpubBuilder());

        var probe = await _probe.ProbeAsync(path, loadCover: true);

        Assert.False(probe.HasCover);
        Assert.Null(probe.CoverBytes);
    }

    [Fact]
    public async Task Probe_LoadCoverFalse_SkipsCoverBytes()
    {
        var path = Save(new EpubBuilder { CoverBytes = EpubBuilder.MakeJpeg() });

        var probe = await _probe.ProbeAsync(path, loadCover: false);

        Assert.Null(probe.CoverBytes);
        Assert.False(probe.HasCover);
    }

    [Fact]
    public async Task Probe_MultiCreators_SplitDedupedAndJoined()
    {
        var path = Save(new EpubBuilder
        {
            Authors = new List<string> { "张三", "李四; 张三", " 王五  " },
        });

        var probe = await _probe.ProbeAsync(path, loadCover: false);

        Assert.Equal("张三; 李四; 王五", probe.Authors);
    }

    [Fact]
    public async Task Probe_Epub3_And_Epub2_AreDistinguished()
    {
        var epub3 = Save(new EpubBuilder { IsEpub3 = true }, "v3.epub");
        var epub2 = Save(new EpubBuilder { IsEpub3 = false }, "v2.epub");

        Assert.True((await _probe.ProbeAsync(epub3, false)).IsEpub3);
        Assert.False((await _probe.ProbeAsync(epub2, false)).IsEpub3);
    }

    [Fact]
    public async Task Probe_DrmMarked_ThrowsDrmDetected()
    {
        var path = Save(new EpubBuilder { MarkEncrypted = true });

        var ex = await Assert.ThrowsAsync<AppException>(
            () => _probe.ProbeAsync(path, loadCover: false));

        Assert.Equal(ErrorCode.DrmDetected, ex.Code);
    }

    [Fact]
    public async Task Probe_NotAZip_ThrowsEpubInvalid_NotRawException()
    {
        var path = Path.Combine(_dir, "garbage.epub");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var ex = await Assert.ThrowsAsync<AppException>(
            () => _probe.ProbeAsync(path, loadCover: false));

        Assert.Equal(ErrorCode.EpubInvalid, ex.Code);
    }

    [Fact]
    public async Task Probe_EmptyFile_ThrowsEpubInvalid()
    {
        var path = Path.Combine(_dir, "empty.epub");
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());

        var ex = await Assert.ThrowsAsync<AppException>(
            () => _probe.ProbeAsync(path, loadCover: false));

        Assert.Equal(ErrorCode.EpubInvalid, ex.Code);
    }

    [Fact]
    public async Task Probe_MissingFile_ThrowsFileNotFound()
    {
        var ex = await Assert.ThrowsAsync<AppException>(
            () => _probe.ProbeAsync(Path.Combine(_dir, "没有这个文件.epub"), loadCover: false));

        Assert.Equal(ErrorCode.FileNotFound, ex.Code);
    }

    [Fact]
    public async Task Probe_TitleMissing_FallsBackToFileName()
    {
        var path = new EpubBuilder { Title = "   " }
            .WriteTo(Path.Combine(_dir, "无名之书.epub"));

        var probe = await _probe.ProbeAsync(path, loadCover: false);

        Assert.False(string.IsNullOrWhiteSpace(probe.Title));
    }

    [Fact]
    public async Task Probe_TruncatedZip_ThrowsEpubInvalid()
    {
        var full = Save(new EpubBuilder { CoverBytes = EpubBuilder.MakeJpeg(600, 900) }, "full.epub");
        var bytes = await File.ReadAllBytesAsync(full);

        // 砍掉 60%：zip 中央目录没了
        var truncated = Path.Combine(_dir, "truncated.epub");
        await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length * 40 / 100)]);

        var ex = await Assert.ThrowsAsync<AppException>(
            () => _probe.ProbeAsync(truncated, loadCover: false));

        Assert.Equal(ErrorCode.EpubInvalid, ex.Code);
    }

    // ---------- 宽松兜底（设计文档 6.1.9，2026-10-02 被真实样本踩出来的） ----------

    [Fact]
    public async Task Probe_HtmlMediaTypeForChapters_FallsBackAndSucceeds()
    {
        // 对应真实文件 69 Sex Positions：manifest 把正文写成 text/html，VersOne 认不出这种正文，
        // 报成"HTML content file ... is missing in the book"（消息是误导的，文件其实在包里）。
        var path = Save(new EpubBuilder
        {
            IsEpub3 = false,
            HtmlMediaTypeForChapters = true,
            Title = "正文写成 text-html 的书",
        });

        var probe = await _probe.ProbeAsync(path, loadCover: false);

        Assert.Equal("正文写成 text-html 的书", probe.Title);
        Assert.True(probe.Damaged, "走兜底读出来的书必须标 Damaged");
    }

    [Fact]
    public async Task Probe_Epub2WithoutSpineToc_FallsBackAndSucceeds()
    {
        // 对应真实文件 Sample Book G：EPUB2 的 spine 缺 toc 属性。
        var path = Save(new EpubBuilder
        {
            IsEpub3 = false,
            OmitSpineToc = true,
            Title = "缺 spine-toc 的书",
        });

        var probe = await _probe.ProbeAsync(path, loadCover: false);

        Assert.Equal("缺 spine-toc 的书", probe.Title);
        Assert.False(probe.IsEpub3);
        Assert.True(probe.Damaged);
    }

    [Fact]
    public async Task Probe_DuplicateManifestHref_FallsBackAndSucceeds()
    {
        // 对应真实文件 Sample Book B 多看精校版：manifest 里同一个 href 声明两次。
        var path = Save(new EpubBuilder
        {
            IsEpub3 = false,
            DuplicateManifestHref = true,
            Title = "manifest href 重复的书",
        });

        var probe = await _probe.ProbeAsync(path, loadCover: false);

        Assert.Equal("manifest href 重复的书", probe.Title);
        Assert.True(probe.Damaged);
    }

    [Fact]
    public async Task Probe_IdpfFontObfuscation_IsNotDrm()
    {
        // 对应真实文件 I love Subject：encryption.xml 只覆盖字体，算法是 IDPF 混淆。
        // 这不是 DRM，正文完全没加密，必须能导入。
        var path = Save(new EpubBuilder
        {
            EncryptionXmlOverride = ObfuscationXml(
                "http://www.idpf.org/2008/embedding", "OEBPS/fonts/book.ttf"),
            Title = "IDPF 字体混淆的书",
        });

        var probe = await _probe.ProbeAsync(path, loadCover: false);

        Assert.Equal("IDPF 字体混淆的书", probe.Title);
        Assert.True(probe.FontObfuscated);
    }

    [Fact]
    public async Task Probe_AdobeFontObfuscation_IsNotDrm()
    {
        var path = Save(new EpubBuilder
        {
            EncryptionXmlOverride = ObfuscationXml(
                "http://ns.adobe.com/pdf/enc#RC", "OEBPS/fonts/book.otf"),
            Title = "Adobe 字体混淆的书",
        });

        var probe = await _probe.ProbeAsync(path, loadCover: false);

        Assert.Equal("Adobe 字体混淆的书", probe.Title);
        Assert.True(probe.FontObfuscated);
    }

    [Fact]
    public async Task Probe_AesEncryptedContent_ThrowsDrmDetectedWithAlgorithmInMessage()
    {
        var path = Save(new EpubBuilder
        {
            EncryptionXmlOverride = ObfuscationXml(
                "http://www.w3.org/2001/04/xmlenc#aes128-cbc", "OEBPS/ch1.xhtml"),
            Title = "真 DRM 的书",
        });

        var ex = await Assert.ThrowsAsync<AppException>(
            () => _probe.ProbeAsync(path, loadCover: false));

        Assert.Equal(ErrorCode.DrmDetected, ex.Code);

        // 提示必须带上算法，不能只说"受 DRM 保护"（设计文档 6.1.9）
        Assert.Contains("aes128-cbc", ex.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_ObfuscationAlgorithmOnContent_StillThrowsDrmDetected()
    {
        // 算法写着字体混淆，但覆盖对象是正文：一样按 DRM 处理
        var path = Save(new EpubBuilder
        {
            EncryptionXmlOverride = ObfuscationXml(
                "http://www.idpf.org/2008/embedding", "OEBPS/ch1.xhtml"),
            Title = "算法对但覆盖正文的书",
        });

        var ex = await Assert.ThrowsAsync<AppException>(
            () => _probe.ProbeAsync(path, loadCover: false));

        Assert.Equal(ErrorCode.DrmDetected, ex.Code);
    }

    [Fact]
    public async Task Probe_LenientPath_StillRejectsZipSlipEntry()
    {
        // 兜底不等于不校验：这本既走了兜底（manifest href 重复），包里又带一个 ../ 条目。
        // T7-2 第 6 项：Zip Slip 在探测阶段就直接拒绝，用专门的 UnsafeArchiveEntry 错误码。
        var path = Save(new EpubBuilder { IsEpub3 = false, DuplicateManifestHref = true });
        AddRawEntry(path, "../evil.txt");

        var ex = await Assert.ThrowsAsync<AppException>(
            () => _probe.ProbeAsync(path, loadCover: false));

        Assert.Equal(ErrorCode.UnsafeArchiveEntry, ex.Code);
    }

    /// <summary>造一份 encryption.xml：指定算法与被覆盖的资源 URI。</summary>
    private static string ObfuscationXml(string algorithm, string uri) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"" +
        " xmlns:enc=\"http://www.w3.org/2001/04/xmlenc#\">" +
        "<enc:EncryptedData>" +
        "<enc:EncryptionMethod Algorithm=\"" + algorithm + "\"/>" +
        "<enc:CipherData><enc:CipherReference URI=\"" + uri + "\"/></enc:CipherData>" +
        "</enc:EncryptedData></encryption>";

    /// <summary>往写好的 epub 里再塞一个条目（用来构造 Zip Slip 这类畸形包）。</summary>
    private static void AddRawEntry(string epubPath, string entryName)
    {
        using var stream = new FileStream(epubPath, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Update);
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write("x");
    }

    private string Save(EpubBuilder builder, string name = "book.epub")
        => builder.WriteTo(Path.Combine(_dir, name));
}