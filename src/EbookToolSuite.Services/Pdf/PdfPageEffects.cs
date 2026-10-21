using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace EbookToolSuite.Services.Pdf;

/// <summary>
/// 页面位图的几个纯变换：白底合成、旋转、反色、"是不是图片页"。全部抽成静态纯函数，
/// 因为这些都是**判定/改写逻辑**，必须能脱离真实 PDF 单测（真实样本里找不到
/// "彩色像素恰好 35%"这种边界样本）。
/// </summary>
/// <remarks>
/// <para>
/// **为什么用 Bgra32**：pdfium 经 Docnet 交出来的就是 BGRA 字节
/// （<c>IPageReader.GetImage()</c> 承诺 width*height*4），中间不换格式，少一次拷贝。
/// </para>
/// <para>
/// **【2026-10-16 纠正一条曾经的错误假设】alpha 不是没用的**：这里原先写着
/// "alpha 一律不参与判定也不被改写，JPEG 结果与 alpha 语义无关"。前半句（不参与**判定**）
/// 仍然成立，后半句是**错的**，并且正是"整页渲成纯黑"那个真 bug 的根源——
/// pdfium 不给页面铺背景，没被内容覆盖的像素保持缓冲区初值（Docnet 给的是全 0），
/// 于是 RGB 与 alpha 同时为 0，JPEG 丢 alpha 后就是纯黑页。
/// 现在进入管线前先做一次 <see cref="FlattenOntoWhite"/>，把"没画过"与"真的画了黑"分开。
/// 反色依旧只动 R/G/B（此时 alpha 已恒为 255）。
/// </para>
/// </remarks>
public static class PdfPageEffects
{
    /// <summary>少于这么多字符（去空白后）就认为这页没有文本层。</summary>
    /// <remarks>
    /// 20 这个阈值的依据：实测「sample-pdf-1」第 0 页文本长度 = 0（整页图），
    /// 第 1 页 = 584 字。真实 PDF 里"有文本层"的页面极少低于 20 字
    /// （连章节标题页都是几十字），而扫描页通常是 0——中间留 20 是为了容忍
    /// 页码/水印这类零星 OCR 残留，它们不该让整页照片被反成底片。
    /// </remarks>
    public const int MinTextLengthForTextPage = 20;

    /// <summary>单像素"算彩色"的通道极差上限：max(R,G,B) - min(R,G,B) 大于它才算彩色。</summary>
    /// <remarks>64 是"明显偏色"的经验线：JPEG 噪声和灰阶扫描件达不到，彩色插画轻松超过。</remarks>
    public const int ColoredChannelSpread = 64;

    /// <summary>抽样像素里彩色占比超过它就认为这页是图片页。</summary>
    /// <remarks>
    /// <para>
    /// **2026-10-16 从 0.35 改成 0.05，依据是实测数据。** 用独立 ctypes 工具
    /// （<c>build\t62b-render.py</c>，先铺白底再渲，max-min 通道 &gt; 64 算彩色）量了四类真实页面：
    /// Calibre 生成 196 页文字版第 0 页 = **0.00%**；
    /// 「sample-pdf-1」第 4 页（文字 + 小插图）= **0.47%**；
    /// 「sample-pdf-1」第 0 页（**整页彩色照片封面**）= **22.13%**；
    /// 「sample-pdf-2」第 0 页（灰阶扫描）= **0.00%**。
    /// </para>
    /// <para>
    /// 原来的 0.35 是**拍脑袋定的**，而且定错了方向：真实整页彩照只有 22% 的像素算"彩色"
    /// （皮肤与柔和色调的饱和度够不到 64），于是照片页被判成文字页、在 Dark 主题下整页反成底片——
    /// 恰好违反设计文档 §6.5.2 要保护的场景（"图片页不反色，否则照片变底片"）。
    /// 0.05 在实测两类之间各留 4 倍以上余量（照片 22% ≫ 5% ≫ 小插图 0.47%）。
    /// </para>
    /// </remarks>
    public const double ColoredPixelRatioThreshold = 0.05;

    /// <summary>抽样预算：约数万个像素就足以判定整页的彩色占比（整页 100 万像素太慢）。</summary>
    private const int SampleBudget = 50_000;

    private static readonly int[] AllowedRotations = [0, 90, 180, 270];

    /// <summary>
    /// 校验并返回旋转角：不是 0/90/180/270 就抛 <see cref="ArgumentOutOfRangeException"/>。
    /// </summary>
    /// <remarks>
    /// 只允许直角是因为旋转是在位图上做的（pdfium 的封装没有公开的设页面旋转路径）：
    /// 任意角度需要重采样，会让文字发虚，而"页面级旋转"在阅读场景里本来就只有直角。
    /// </remarks>
    public static int NormalizeRotation(int rotationDegrees)
    {
        if (Array.IndexOf(AllowedRotations, rotationDegrees) >= 0)
        {
            return rotationDegrees;
        }

        throw new ArgumentOutOfRangeException(
            nameof(rotationDegrees),
            rotationDegrees,
            "PDF 页面旋转角只支持 0 / 90 / 180 / 270（顺时针）。");
    }

    /// <summary>
    /// 这页是不是"图片页"。两条或判据，任一成立即是。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据一：<paramref name="text"/> 去掉空白后长度 &lt; <see cref="MinTextLengthForTextPage"/>。
    /// 没有文本层 = 扫描件/整页图，这是绝大多数情况（实测「sample-pdf-2」「sample-pdf-3」
    /// 两份纯扫描样本每页都是 0 字）。
    /// </para>
    /// <para>
    /// 判据二：抽样像素里彩色占比 &gt; <see cref="ColoredPixelRatioThreshold"/>。
    /// 覆盖"有文本层但主体是彩色照片"的混合页（图文混排的烹饪书、漫画、画册）。
    /// 这两条都只能判"像不像照片"，判错了的代价是**只影响 Dark 主题反不反色**：
    /// 判成图片页 → 不反色（照片正常，文字仍是深色底上深色字，不好看但可读）；
    /// 判成文字页 → 反色（照片变底片，明显更糟）。所以规则刻意偏向"宁可误判成图片页"。
    /// **阈值必须是实测定的**：2026-10-16 的实测把原来的 0.35 否掉了，详见常量上的注释。
    /// </para>
    /// <para>
    /// **已知缺口（如实记，本轮样本里没有实例）**：灰阶照片/扫描页**带 OCR 文本层**时，
    /// 彩色占比是 0（判据二挡不住）、文本又超过 20 字（判据一挡不住）→ 仍会在 Dark 主题下被反色。
    /// 数据支持的下一条判据是"纸白占比 &lt; 60%"（实测：文字页 92%、彩色照片 45%、灰阶扫描 36%），
    /// 但那需要两页带 OCR 文本层的灰阶扫描件才能验证，本轮没有，所以**先不写没有实测支撑的规则**。
    /// </para>
    /// </remarks>
    /// <param name="text">该页的纯文本（pdfium <c>GetText()</c>），可为 null。</param>
    /// <param name="image">该页的位图；为 null 时只按文本判。</param>
    public static bool IsImagePage(string? text, Image<Bgra32>? image)
    {
        if ((text ?? string.Empty).Trim().Length < MinTextLengthForTextPage)
        {
            return true;
        }

        return ColoredPixelRatio(image) > ColoredPixelRatioThreshold;
    }

    /// <summary>
    /// 抽样统计"彩色像素"占比，结果在 [0,1]。抽样步长由像素总数与
    /// <see cref="SampleBudget"/> 决定，是确定性的（同一张图每次结果一样，可单测）。
    /// </summary>
    /// <param name="image">位图；null 或尺寸为 0 时返回 0（"看不出彩色"）。</param>
    public static double ColoredPixelRatio(Image<Bgra32>? image)
    {
        if (image is null || image.Width <= 0 || image.Height <= 0)
        {
            return 0;
        }

        var step = SampleStride(image.Width, image.Height);
        long sampled = 0;
        long colored = 0;

        for (var y = 0; y < image.Height; y += step)
        {
            for (var x = 0; x < image.Width; x += step)
            {
                var pixel = image[x, y];
                var max = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                var min = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));

                sampled++;

                if (max - min > ColoredChannelSpread)
                {
                    colored++;
                }
            }
        }

        return sampled == 0 ? 0 : (double)colored / sampled;
    }

    /// <summary>抽样步长：让抽样点数落在大约 <see cref="SampleBudget"/> 附近，至少 1。</summary>
    private static int SampleStride(int width, int height)
    {
        var total = (long)width * height;

        return total <= SampleBudget ? 1 : Math.Max(1, (int)Math.Sqrt(total / (double)SampleBudget));
    }

    /// <summary>
    /// 把 BGRA 缓冲**原位**合成到白底上：<c>c' = c·a/255 + 255·(1 - a/255)</c>，并把 alpha 置 255。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 【为什么必须做：这是实测撞出来的真 bug】pdfium **不给页面铺背景**——它只画 PDF 内容，
    /// 没被内容覆盖的像素保持缓冲区初值；Docnet.Core 的 <c>GetImage()</c> 交出来的缓冲区是全 0，
    /// 于是"没有自绘背景的页面"整页是 (0,0,0,0)，JPEG 丢掉 alpha 之后就是**纯黑页**。
    /// 实测证据（<c>build\t62b-render.py</c> 用独立 ctypes 直接调 pdfium，同一页渲两次，
    /// 只有"渲之前铺不铺白底"这一个变量）：
    /// Calibre 生成的 196 页文字版第 0 页 = 不铺白底 **100% 黑** → 铺白底 **92.26% 白 / 4.81% 黑**；
    /// 「sample-pdf-1」第 4 页 = 70.67% 黑 → 93.66% 白。
    /// 真实 PDF 的封面往往整页是图、恰好盖满，所以肉眼一直没暴露（T6-2 的封面判据只查了"体积大于几 KB"）。
    /// </para>
    /// <para>
    /// 【为什么按 alpha 合成，而不是"把黑像素刷成白"】alpha 恰好把两种情况分开了：
    /// 没画过的地方 <c>a=0</c>（要变白），PDF 真的画了黑色的地方 <c>a=255</c>（必须保持黑）。
    /// 不做区分地刷白会把黑字一起抹掉。
    /// </para>
    /// </remarks>
    public static void FlattenOntoWhite(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);

        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];

            if (alpha == 255)
            {
                continue;
            }

            if (alpha == 0)
            {
                bgra[i] = 255;
                bgra[i + 1] = 255;
                bgra[i + 2] = 255;
                bgra[i + 3] = 255;
                continue;
            }

            var inverse = 255 - alpha;

            bgra[i] = (byte)((bgra[i] * alpha + 255 * inverse) / 255);
            bgra[i + 1] = (byte)((bgra[i + 1] * alpha + 255 * inverse) / 255);
            bgra[i + 2] = (byte)((bgra[i + 2] * alpha + 255 * inverse) / 255);
            bgra[i + 3] = 255;
        }
    }

    /// <summary>
    /// 原位反色（Dark 主题的像素实现）：<c>c' = 255 - c</c>，只动 R/G/B。
    /// </summary>
    /// <remarks>
    /// 【取舍：为什么不做 CSS 里那半步 hue-rotate(180deg)】
    /// 前端惯用的 <c>filter: invert(1) hue-rotate(180deg)</c> 里，hue-rotate 的作用是
    /// 把反色后偏掉的色相扳回来，让彩色插画"还是那个颜色"。本引擎**图片页根本不反色**，
    /// 剩下要反的就是黑白文字页，此时色相没有可扳的东西；多加一步矩阵运算只会
    /// 让黑字白底反成略微偏色的白底。所以这里就用最朴素的 RGB 反色，
    /// 与需求里"invert(1) 的像素等价物就是 RGB 反色"一致。
    /// 另一个可选方案是"亮度映射"（把背景映射到深灰而不是纯黑），代价是文字对比度下降、
    /// 且要引入可配置的映射曲线；在只有黑白文字页要处理的前提下，反色已经够用。
    /// </remarks>
    public static void InvertRgb(Image<Bgra32> image)
    {
        ArgumentNullException.ThrowIfNull(image);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);

                for (var x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    pixel.R = (byte)(255 - pixel.R);
                    pixel.G = (byte)(255 - pixel.G);
                    pixel.B = (byte)(255 - pixel.B);
                }
            }
        });
    }

    /// <summary>
    /// 按直角角度旋转位图（顺时针），返回新位图；0 度时原样返回入参（不拷贝）。
    /// </summary>
    /// <remarks>
    /// 用 ImageSharp 的 <see cref="RotateMode"/> 而不是 <c>Rotate(float)</c>：
    /// 前者是整数重排（无插值、无精度损失），后者走重采样矩阵，90 度也会轻微糊边。
    /// </remarks>
    public static Image<Bgra32> Rotate(Image<Bgra32> image, int rotationDegrees)
    {
        ArgumentNullException.ThrowIfNull(image);

        var mode = NormalizeRotation(rotationDegrees) switch
        {
            90 => RotateMode.Rotate90,
            180 => RotateMode.Rotate180,
            270 => RotateMode.Rotate270,
            _ => RotateMode.None,
        };

        if (mode == RotateMode.None)
        {
            return image;
        }

        image.Mutate(context => context.Rotate(mode));

        return image;
    }
}
