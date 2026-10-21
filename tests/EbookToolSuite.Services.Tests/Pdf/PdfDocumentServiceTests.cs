using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Docnet.Core.Models;
using EbookToolSuite.Services.Pdf;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit.Abstractions;

namespace EbookToolSuite.Services.Tests.Pdf;

/// <summary>
/// T6-2 的实测：对 <c>TestCorpus/</c> 下的真实 PDF 样本跑通「取总页数」与「渲染第 1 页」。
/// </summary>
/// <remarks>
/// <para>
/// **为什么是集成测试而不是单元测试**：这个任务的验收标准就是"真 pdfium 能读真 PDF"。
/// 用假的 reader 只能证明异常翻译逻辑对，证明不了 Docnet.Core 的封送/原生库加载是对的，
/// 所以这里坚持走真实文件、真实原生库。
/// </para>
/// <para>
/// **样本缺失就明确失败**，不 Skip：这三份 PDF 是 T6-2 判据的实测对象，
/// 悄悄跳过等于把判据变成空转（仓库里已有"数层数很脆，之前就数错过一次"的教训）。
/// </para>
/// <para>
/// 三份样本都是无加密的正常 PDF，预期全部读通；若哪天某份因加密/损坏读不出来，
/// 本测试会<strong>如实失败</strong>并把 <see cref="AppException.Code"/> 打出来，不去改成假通过。
/// </para>
/// </remarks>
public sealed class PdfDocumentServiceTests
{
    /// <summary>长边上限，与设计文档 §6.5.4 交给封面管线的尺寸一致。</summary>
    private const int MaxLongEdge = 1200;

    private readonly ITestOutputHelper _output;

    public PdfDocumentServiceTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 真实样本文件名（含空格、括号、日文、繁体中文）。
    /// 单独放一个数组：<see cref="TheoryData{T}"/> 只能喂给 xunit 的 Theory，
    /// 测试体里想遍历样本时可以直接用这个数组（对它做 LINQ 类型推断才正常）。
    /// </summary>
    private static readonly string[] CorpusFiles =
    [
        "sample-pdf-1.pdf",
        "sample-pdf-2.pdf",
        "sample-pdf-3.pdf",
    ];

    /// <summary>喂给 Theory 的样本矩阵。</summary>
    public static TheoryData<string> CorpusNames()
    {
        var data = new TheoryData<string>();

        foreach (var name in CorpusFiles)
        {
            data.Add(name);
        }

        return data;
    }

    private static IPdfDocumentService CreateService() => new PdfDocumentService(new PdfiumLibrary());

    /// <summary>
    /// 逐份断言：总页数 &gt; 0，并把实测页数打印到测试输出里留档。
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void GetPageCount_真实样本_返回大于零的页数(string fileName)
    {
        var path = CorpusPath(fileName);
        var service = CreateService();

        var pageCount = service.GetPageCount(path);

        // 实测值进输出，方便对着判据核对（56/320/129 这类数字要能被人看到）。
        _output.WriteLine($"{fileName} → GetPageCount = {pageCount}");

        Assert.True(pageCount > 0, $"{fileName} 的页数应大于 0，实际 {pageCount}");
    }

    /// <summary>
    /// 逐份断言：渲染第 1 页返回非空字节，且是完整 JPEG（magic bytes + 结尾标记）。
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void RenderPage_真实样本_返回完整JPEG字节(string fileName)
    {
        var path = CorpusPath(fileName);
        var service = CreateService();

        var bytes = service.RenderPage(path, 0, MaxLongEdge);

        _output.WriteLine($"{fileName} → RenderPage(0, {MaxLongEdge}) = {bytes.Length} 字节");

        Assert.True(bytes.Length > 0, $"{fileName} 渲染出的字节数为 0");

        // JPEG 结构自查：SOI(FFD8) 开头、EOI(FFD9) 结尾。只校验 magic bytes 也允许，
        // 但加了结尾标记能一并挡住"写到一半被截断"的情况。
        Assert.True(bytes.Length > 4, $"{fileName} 的 JPEG 太短，只有 {bytes.Length} 字节");
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        Assert.Equal(0xFF, bytes[^2]);
        Assert.Equal(0xD9, bytes[^1]);
    }

    /// <summary>
    /// 逐份断言：渲染结果能被 ImageSharp 解码成图片，且长边不超过请求的 <see cref="MaxLongEdge"/>。
    /// 这一条才真正证明"返回的字节是给封面管线用的图"，而不是一段凑巧有正确头尾的噪声。
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void RenderPage_真实样本_能被ImageSharp解码且长边不超限(string fileName)
    {
        var path = CorpusPath(fileName);
        var service = CreateService();

        var bytes = service.RenderPage(path, 0, MaxLongEdge);

        using var image = Image.Load(bytes);
        var longEdge = Math.Max(image.Width, image.Height);

        _output.WriteLine($"{fileName} → 图片 {image.Width}x{image.Height}，长边 {longEdge}");

        Assert.True(longEdge > 0, "解码出的图片尺寸不应为 0");
        Assert.True(
            longEdge <= MaxLongEdge,
            $"{fileName} 的长边 {longEdge} 超过了请求的上限 {MaxLongEdge}");
    }

    /// <summary>
    /// 三份样本的页数必须互不相同且都 &gt; 0：如果实现把某个常量写死返回，
    /// 这条会立刻炸掉（逐份断言反而挡不住"永远返回 57"这种错）。
    /// </summary>
    [Fact]
    public void GetPageCount_三份样本页数互不相同_证明不是写死的常量()
    {
        var service = CreateService();

        var counts = CorpusFiles
            .Select(name => service.GetPageCount(CorpusPath(name)))
            .ToArray();

        _output.WriteLine("三份样本页数 = " + string.Join(", ", counts));

        Assert.All(counts, c => Assert.True(c > 0, $"页数应大于 0，实际 {c}"));
        Assert.Equal(counts.Length, counts.Distinct().Count());
    }

    /// <summary>文件不存在 → <see cref="ErrorCode.FileNotFound"/>，绝不返回 0。</summary>
    [Fact]
    public void GetPageCount_文件不存在_抛AppException且错误码为FileNotFound()
    {
        var service = CreateService();
        var missing = Path.Combine(CorpusDirectory(), "这个文件不存在-9f3a1c.pdf");

        var ex = Assert.Throws<AppException>(() => service.GetPageCount(missing));

        _output.WriteLine($"FileNotFound → Code={ex.Code} / {ex.UserMessage}");
        Assert.Equal(ErrorCode.FileNotFound, ex.Code);
    }

    /// <summary>
    /// 不是 PDF（只有 zip 签名）→ 归 <see cref="ErrorCode.CorruptFile"/> 或
    /// <see cref="ErrorCode.UnsupportedFormat"/>，两者对用户都是"这不是有效的 PDF"。
    /// </summary>
    [Fact]
    public void GetPageCount_不是PDF_抛AppException()
    {
        var service = CreateService();
        var fake = Path.Combine(Path.GetTempPath(), "t62-fake-" + Guid.NewGuid().ToString("N") + ".pdf");

        try
        {
            // 只写 zip 签名，确保它不是任何合法 PDF 的开头。
            File.WriteAllBytes(fake, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00]);

            var ex = Assert.Throws<AppException>(() => service.GetPageCount(fake));

            _output.WriteLine($"非 PDF → Code={ex.Code} / {ex.UserMessage}");
            Assert.True(
                ex.Code is ErrorCode.CorruptFile or ErrorCode.UnsupportedFormat,
                $"期望 CorruptFile 或 UnsupportedFormat，实际 {ex.Code}");
        }
        finally
        {
            File.Delete(fake);
        }
    }

    /// <summary>
    /// 页码越界 → <see cref="ErrorCode.UnsupportedFormat"/>。
    /// 这不是"文件坏了"，所以刻意与损坏文件用不同的错误码，UI 才能给对提示。
    /// </summary>
    [Fact]
    public void RenderPage_页码越界_抛AppException且错误码为UnsupportedFormat()
    {
        var service = CreateService();
        var path = CorpusPath(CorpusFiles[0]);

        var ex = Assert.Throws<AppException>(() => service.RenderPage(path, 99999, MaxLongEdge));

        _output.WriteLine($"越界 → Code={ex.Code} / {ex.UserMessage}");
        Assert.Equal(ErrorCode.UnsupportedFormat, ex.Code);
    }

    /// <summary>
    /// 加密 PDF → <see cref="ErrorCode.DrmDetected"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **本机样本里没有加密 PDF**（设计文档提到的 pdf-encrypted.pdf 不存在），
    /// 所以这里在运行时手工拼一份真正用 RC4 40-bit（/V 1 /R 2）加密的单页 PDF 当样本。
    /// 这是"没有真实样本时构造样本"覆盖 DrmDetected 分支，不是跳过、也不是假通过：
    /// pdfium 确实拒绝打开它，走的是与真实加密文件相同的失败路径。
    /// </para>
    /// <para>
    /// 判据的边界要说清：识别加密靠的是 pdfium 报错文本里的 password/encrypt 字样
    /// （代码里刻意没有写死错误码数值）。本机实测这份构造样本触发的正是
    /// <c>ErrorCode=4, "password required or incorrect password"</c>，真实加密文件走同一条失败路径。
    /// </para>
    /// </remarks>
    [Fact]
    public void GetPageCount_加密PDF_抛AppException且错误码为DrmDetected()
    {
        var service = CreateService();
        var encrypted = Path.Combine(Path.GetTempPath(), "t62-enc-" + Guid.NewGuid().ToString("N") + ".pdf");

        try
        {
            File.WriteAllBytes(encrypted, BuildRc4EncryptedPdf());

            var ex = Assert.Throws<AppException>(() => service.GetPageCount(encrypted));

            _output.WriteLine($"加密 PDF → Code={ex.Code} / {ex.UserMessage}");
            _output.WriteLine($"technical = {ex.Message}");
            Assert.Equal(ErrorCode.DrmDetected, ex.Code);
        }
        finally
        {
            File.Delete(encrypted);
        }
    }

    /// <summary>
    /// 组合根那条注册路径本身要能解析出服务，且是单例——
    /// 否则"可被 DI 注册"就只是纸上的一句话。
    /// </summary>
    [Fact]
    public void AddPdfServices_能被容器解析且为单例()
    {
        var services = new ServiceCollection();
        var returned = services.AddPdfServices();

        Assert.Same(services, returned);

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IPdfDocumentService>();
        var second = provider.GetRequiredService<IPdfDocumentService>();

        Assert.Same(first, second);
        Assert.Same(
            provider.GetRequiredService<IPdfiumLibrary>(),
            provider.GetRequiredService<IPdfiumLibrary>());

        var pageCount = first.GetPageCount(CorpusPath(CorpusFiles[0]));
        _output.WriteLine($"DI 解析出的实例读到页数 = {pageCount}");
        Assert.True(pageCount > 0);
    }

    // ---------------- 路径与样本构造 ----------------

    /// <summary>
    /// 三份样本到底有没有**文本层**。
    ///
    /// 为什么要专门测这个：T6-2 判据 1 的"文本可选中"只能靠"真实 Ctrl+A / Ctrl+C + 读剪贴板"
    /// 从外部观察（文本层在跨源插件里，脚本进不去）。如果文件本身就是扫描图、没有文本层，
    /// 那剪贴板当然是空的——**那是样本的性质，不是产品缺陷**，两者必须分清。
    /// 这一条同时是 T6-2B"文本层 → 阅读器内搜索 + 全库 FTS5"的依据。
    /// </summary>
    [Fact]
    public void 真实样本_文本层字符数()
    {
        var library = new PdfiumLibrary();
        var lines = new List<string>();

        foreach (var file in Directory.GetFiles(CorpusDirectory(), "*.pdf")
                     .OrderBy(f => new FileInfo(f).Length))
        {
            using var reader = library.GetDocReader(file, new PageDimensions(1, 1));

            var pages = reader.GetPageCount();
            var sampled = Math.Min(pages, 10);
            var chars = 0;

            for (var i = 0; i < sampled; i++)
            {
                using var page = reader.GetPageReader(i);
                chars += (page.GetText() ?? string.Empty).Trim().Length;
            }

            lines.Add($"{Path.GetFileName(file)}：{pages} 页，抽样前 {sampled} 页共 {chars} 字");
        }

        foreach (var line in lines)
        {
            _output.WriteLine(line);
        }

        Assert.NotEmpty(lines);
    }

    /// <summary><c>TestCorpus/</c> 的绝对路径；找不到就抛，避免测试静默跑空。</summary>
    private static string CorpusDirectory()
    {
        var root = FindRepoRoot()
            ?? throw new InvalidOperationException(
                "找不到仓库根（应从测试输出目录往上找到含 Tools/manifest.json 的目录）。");

        var corpus = Path.Combine(root, "TestCorpus");

        if (!Directory.Exists(corpus))
        {
            throw new DirectoryNotFoundException($"找不到样本目录：{corpus}");
        }

        return corpus;
    }

    private static string CorpusPath(string fileName)
    {
        var path = Path.Combine(CorpusDirectory(), fileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"T6-2 的实测样本缺失，无法验证 pdfium 通路：{path}", path);
        }

        return path;
    }

    /// <summary>往上找带 Tools\manifest.json 的目录 = 仓库根（与仓库既有测试同一做法）。</summary>
    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tools", "manifest.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// 手工拼一份真正用 RC4 40-bit（/V 1 /R 2）加密的单页 PDF，覆盖 DrmDetected 分支。
    /// </summary>
    /// <remarks>
    /// 不追求口令能被解开：pdfium 只要在 trailer 里看到 /Encrypt 就会拒绝打开，
    /// 而我们要验证的正是"拒绝打开被翻译成 DrmDetected"。
    /// 不写入仓库，只落在临时目录，测完即删。
    /// </remarks>
    private static byte[] BuildRc4EncryptedPdf()
    {
        // 40-bit RC4 的 /O 值：前 5 字节是口令的 RC4 加密结果，其余按规范补 0xFF。
        var owner = new byte[32];

        for (var i = 0; i < owner.Length; i++)
        {
            owner[i] = i < 5 ? (byte)(0xA0 + i) : (byte)0xFF;
        }

        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"),
            Encoding.ASCII.GetBytes(
                "<< /Filter /Standard /V 1 /R 2 /Length 40 /P -1 /O <" + Convert.ToHexString(owner) +
                "> /U <" + Convert.ToHexString(owner) + "> >>"),
        };

        using var ms = new MemoryStream();

        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n");

        var offsets = new long[objects.Count + 1];

        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n");
            ms.Write(objects[i]);
            Write("\nendobj\n");
        }

        var xrefOffset = ms.Position;

        Write($"xref\n0 {objects.Count + 1}\n");
        Write("0000000000 65535 f \n");

        for (var i = 1; i <= objects.Count; i++)
        {
            Write($"{offsets[i]:D10} 00000 n \n");
        }

        Write(
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Encrypt 4 0 R " +
            "/ID [<0123456789abcdef0123456789abcdef> <0123456789abcdef0123456789abcdef>] >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// T6-2B 回归：**页面自己不画背景的 PDF 不能渲成黑页**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 守的是 2026-10-16 实测撞出来的真 bug：pdfium **不给页面铺背景**，没被内容覆盖的像素
    /// 保持缓冲区初值（Docnet.Core 交出来是全 0），于是"页面自己不画白底"的 PDF 整页变成
    /// (0,0,0,0)，JPEG 丢掉 alpha 之后就是**纯黑页**。修法是进管线前按 alpha 合成到白底
    /// （<c>PdfPageEffects.FlattenOntoWhite</c>）。
    /// </para>
    /// <para>
    /// 样本 <c>t62b-text-200p.pdf</c> 正是这一类（Calibre 输出的文字版）：实测未修之前
    /// **整页 100% 黑**，修复后白像素约 92%。断言只取"白像素过半"，不写死 92%——
    /// 具体数值随字号与页边距变，而这条测试要守的是"是不是黑页"，不是精确排版。
    /// </para>
    /// </remarks>
    [Fact]
    public void RenderPage_页面不自绘背景的PDF_渲出来不是黑页()
    {
        var path = PdfTestCorpus.CorpusPath(PdfTestCorpus.TextBook200Pages);
        var service = CreateService();

        var image = service.RenderPage(path, 0, new PdfPageRenderOptions(1200, 0, PdfPageTheme.Normal));

        Assert.True(image.Width > 0 && image.Height > 0);

        using var bitmap = Image.Load<Rgba32>(image.Jpeg);

        long white = 0;
        long total = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap[x, y];

                total++;

                if (pixel.R > 200 && pixel.G > 200 && pixel.B > 200)
                {
                    white++;
                }
            }
        }

        var ratio = total == 0 ? 0 : (double)white / total;

        _output.WriteLine($"白像素占比 {ratio:P2}（{bitmap.Width}x{bitmap.Height}）");

        Assert.True(
            ratio > 0.5,
            $"渲出来不是白底（白像素只有 {ratio:P2}）——多半是没做白底合成，整页变黑了。");
    }

    /// <summary>
    /// T6-2B 判据（设计文档 §6.5.2）：「按主题渲染页面，**图片页不反色**」——用真实扫描件钉死。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 扫描件的每一页都是整页位图，把它反色等于把整本书变成底片（用户看到的就是"图片显示不正常"）。
    /// 这条用**像素等价**来断言，而不是只看 <c>IsImagePage</c> 这个布尔：如果反色真的执行了，
    /// 同一页在 Dark 与 Normal 下的像素必然互补，编码出来的 JPEG 字节不可能相同。
    /// </para>
    /// <para>
    /// 混合页（文字 + 彩色插图）的判定另说：<c>IsImagePage</c> 靠"文本长度 &lt; 20 或彩色像素占比 &gt; 35%"，
    /// 文本多于 20 字的插图页会被当成文字页——那属于既有启发式的取舍，本测试只**打印**它的实测值，
    /// 不把猜测写成断言（免得把错误期望固化成"标准"）。
    /// </para>
    /// </remarks>
    [Fact]
    public void RenderPage_纯扫描页_暗色主题下不反色()
    {
        var path = PdfTestCorpus.CorpusPath(PdfTestCorpus.ScannedBook38M);
        var service = CreateService();

        var normal = service.RenderPage(path, 0, new PdfPageRenderOptions(1200, 0, PdfPageTheme.Normal));
        var dark = service.RenderPage(path, 0, new PdfPageRenderOptions(1200, 0, PdfPageTheme.Dark));

        var mixed = service.RenderPage(
            PdfTestCorpus.CorpusPath(PdfTestCorpus.TextBook),
            4,
            new PdfPageRenderOptions(1200, 0, PdfPageTheme.Normal));

        _output.WriteLine(
            $"扫描件第 0 页：IsImagePage={normal.IsImagePage}，Normal/Dark 字节 {normal.Jpeg.Length}/{dark.Jpeg.Length}");

        _output.WriteLine(
            $"混合页（萌版第 4 页）：IsImagePage={mixed.IsImagePage}，" +
            $"文本层 {service.ReadPageText(PdfTestCorpus.CorpusPath(PdfTestCorpus.TextBook), 4).Text.Length} 字");

        Assert.True(normal.IsImagePage, "整页扫描件必须被判成图片页，否则暗色主题会把它反成底片。");
        Assert.Equal(normal.Jpeg, dark.Jpeg);
    }

    /// <summary>
    /// T6-2B（设计文档 §6.5.2）：**整页彩色照片的封面页在暗色主题下也不能被反色**。
    /// </summary>
    /// <remarks>
    /// 这条是"阈值定错方向"那个真 bug 的回归测试：原阈值 0.35 是拍脑袋定的，
    /// 而真实整页彩照（「sample-pdf-1」第 0 页）实测只有 **22.13%** 的像素算"彩色"
    /// （皮肤与柔和色调的饱和度够不到 64），于是它被判成文字页、Dark 主题下整页反成底片。
    /// 断言用**字节相等**：没反色时同一页在 Normal/Dark 下的编码结果完全一致。
    /// </remarks>
    [Fact]
    public void RenderPage_整页彩色照片页_暗色主题下不反色()
    {
        var path = PdfTestCorpus.CorpusPath(PdfTestCorpus.TextBook);
        var service = CreateService();

        var normal = service.RenderPage(path, 0, new PdfPageRenderOptions(1200, 0, PdfPageTheme.Normal));
        var dark = service.RenderPage(path, 0, new PdfPageRenderOptions(1200, 0, PdfPageTheme.Dark));

        _output.WriteLine(
            $"封面页 IsImagePage={normal.IsImagePage}；Normal/Dark 字节 {normal.Jpeg.Length}/{dark.Jpeg.Length}");

        Assert.True(normal.IsImagePage, "整页彩色照片被判成文字页了——Dark 主题会把它反成底片。");
        Assert.Equal(normal.Jpeg, dark.Jpeg);
    }

    /// <summary>
    /// 对称验证：纯文字页在暗色主题下**必须**被反色（白底黑字 → 黑底白字），否则"按主题渲染"没落地。
    /// </summary>
    [Fact]
    public void RenderPage_纯文字页_暗色主题下被反色()
    {
        var path = PdfTestCorpus.CorpusPath(PdfTestCorpus.TextBook200Pages);
        var service = CreateService();

        var normal = service.RenderPage(path, 0, new PdfPageRenderOptions(1200, 0, PdfPageTheme.Normal));
        var dark = service.RenderPage(path, 0, new PdfPageRenderOptions(1200, 0, PdfPageTheme.Dark));

        Assert.False(normal.IsImagePage, "纯文字版式页不该被判成图片页。");
        Assert.NotEqual(normal.Jpeg, dark.Jpeg);

        using var normalImage = Image.Load<Rgba32>(normal.Jpeg);
        using var darkImage = Image.Load<Rgba32>(dark.Jpeg);

        var normalLuma = MeanLuma(normalImage);
        var darkLuma = MeanLuma(darkImage);

        _output.WriteLine($"平均亮度：Normal={normalLuma:F1} Dark={darkLuma:F1}");

        Assert.True(normalLuma > 200, $"文字页正常主题下应当是亮底（实测 {normalLuma:F1}）。");
        Assert.True(darkLuma < 90, $"文字页暗色主题下应当反成暗底（实测 {darkLuma:F1}）。");
    }

    private static double MeanLuma(Image<Rgba32> image)
    {
        long sum = 0;
        long count = 0;

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image[x, y];

                sum += (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000;
                count++;
            }
        }

        return count == 0 ? 0 : (double)sum / count;
    }
}
