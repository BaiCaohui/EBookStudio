using EbookToolSuite.Services.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EbookToolSuite.Services.Tests.Pdf;

/// <summary>
/// T6-2B 的三个纯变换：旋转角归一化、反色、图片页判定。
/// </summary>
/// <remarks>
/// 这些判定必须能脱离真实 PDF 单测——真实样本里找不到"彩色像素恰好 35%"
/// "通道极差恰好等于 64"这类边界样本，样本也没法用来验证旋转的像素级方向。
/// </remarks>
public sealed class PdfPageEffectsTests
{
    // ---------------- 旋转角 ----------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void 旋转角_合法值原样返回(int degrees)
    {
        Assert.Equal(degrees, PdfPageEffects.NormalizeRotation(degrees));
    }

    [Theory]
    [InlineData(45)]
    [InlineData(1)]
    [InlineData(-90)]
    [InlineData(360)]
    public void 旋转角_非法值抛ArgumentOutOfRangeException(int degrees)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageEffects.NormalizeRotation(degrees));
    }

    // ---------------- 旋转 ----------------

    /// <summary>构造 4 宽 2 高的小图：左下角红、左上角绿、其余蓝。</summary>
    private static Image<Bgra32> Build4x2()
    {
        var image = new Image<Bgra32>(4, 2);

        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                image[x, y] = new Bgra32(0, 0, 255);
            }
        }

        image[0, 1] = new Bgra32(0, 0, 255, 255);   // 左下角：蓝（占位，下面覆盖）
        image[0, 0] = new Bgra32(0, 255, 0);        // 左上角：绿
        image[0, 1] = new Bgra32(255, 0, 0);        // 左下角：红

        return image;
    }

    [Fact]
    public void 旋转_0度返回同一实例()
    {
        using var image = Build4x2();

        Assert.Same(image, PdfPageEffects.Rotate(image, 0));
    }

    [Fact]
    public void 旋转_90度交换宽高且是顺时针()
    {
        using var image = Build4x2();

        PdfPageEffects.Rotate(image, 90);

        Assert.Equal(2, image.Width);
        Assert.Equal(4, image.Height);

        // 顺时针：源左下角 (0,1) 转到目标左上角 (0,0)；源左上角 (0,0) 转到目标 (1,0)。
        var topLeft = image[0, 0];
        var topRight = image[1, 0];

        Assert.Equal(255, topLeft.R);
        Assert.Equal(0, topLeft.G);
        Assert.Equal(0, topLeft.B);

        Assert.Equal(0, topRight.R);
        Assert.Equal(255, topRight.G);
    }

    [Fact]
    public void 旋转_180度保持宽高且上下颠倒()
    {
        using var image = Build4x2();

        PdfPageEffects.Rotate(image, 180);

        Assert.Equal(4, image.Width);
        Assert.Equal(2, image.Height);

        // 源 (0,0) 绿 → 目标 (3,1)
        var mirrored = image[3, 1];

        Assert.Equal(0, mirrored.R);
        Assert.Equal(255, mirrored.G);
    }

    [Fact]
    public void 旋转_270度交换宽高()
    {
        using var image = Build4x2();

        PdfPageEffects.Rotate(image, 270);

        Assert.Equal(2, image.Width);
        Assert.Equal(4, image.Height);
    }

    [Fact]
    public void 旋转_非法角度抛ArgumentOutOfRangeException()
    {
        using var image = Build4x2();

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageEffects.Rotate(image, 45));
    }

    // ---------------- 反色 ----------------

    [Fact]
    public void 反色_只动RGB_alpha原样保留()
    {
        using var image = new Image<Bgra32>(2, 1);
        image[0, 0] = new Bgra32(10, 20, 30, 40);
        image[1, 0] = new Bgra32(0, 128, 255, 0);

        PdfPageEffects.InvertRgb(image);

        Assert.Equal(new Bgra32(245, 235, 225, 40), image[0, 0]);

        // alpha=0 的像素也要照样反色（pdfium 的 alpha 不保证有意义，不能拿它当"透明所以跳过"）
        Assert.Equal(new Bgra32(255, 127, 0, 0), image[1, 0]);
    }

    // ---------------- 图片页判定：文本长度 ----------------

    [Fact]
    public void 图片页判定_无文本层_判为图片页()
    {
        // 实测「萌版」第 0 页与两份纯扫描样本的每一页都是这种形态。
        Assert.True(PdfPageEffects.IsImagePage("", null));
        Assert.True(PdfPageEffects.IsImagePage(null, null));
        Assert.True(PdfPageEffects.IsImagePage("   \n\t ", null));
    }

    [Fact]
    public void 图片页判定_文本长度恰好19_判为图片页()
    {
        Assert.True(PdfPageEffects.IsImagePage(new string('字', 19), null));
    }

    [Fact]
    public void 图片页判定_文本长度恰好20_不是图片页()
    {
        Assert.False(PdfPageEffects.IsImagePage(new string('字', 20), null));
    }

    // ---------------- 图片页判定：彩色占比 ----------------

    /// <summary>造 <paramref name="colored"/> 个纯红像素，其余纯白，宽度 = colored + white。</summary>
    private static Image<Bgra32> BuildStrip(int colored, int white)
    {
        var image = new Image<Bgra32>(colored + white, 1);

        for (var x = 0; x < colored; x++)
        {
            image[x, 0] = new Bgra32(255, 0, 0);
        }

        for (var x = colored; x < colored + white; x++)
        {
            image[x, 0] = new Bgra32(255, 255, 255);
        }

        return image;
    }

    [Fact]
    public void 彩色占比_50个彩色像素_占比为0点5()
    {
        using var image = BuildStrip(50, 50);

        Assert.Equal(0.5, PdfPageEffects.ColoredPixelRatio(image), 6);
        Assert.True(PdfPageEffects.IsImagePage(new string('字', 100), image));
    }

    [Fact]
    public void 彩色占比_恰好等于阈值_不判为图片页()
    {
        /* 阈值语义是"严格大于"，所以边界值本身不算图片页。
           数量**由常量推出来**、不写死数字：这个阈值本来就是要随实测调整的旋钮
           （2026-10-16 刚从拍脑袋的 0.35 改成有实测支撑的 0.05），
           写死数字会让"调阈值"必然弄红这条测试，反而掩盖了它真正要守的语义。 */
        var boundary = (int)Math.Round(PdfPageEffects.ColoredPixelRatioThreshold * 100);

        using var image = BuildStrip(boundary, 100 - boundary);

        Assert.Equal(PdfPageEffects.ColoredPixelRatioThreshold, PdfPageEffects.ColoredPixelRatio(image), 6);
        Assert.False(PdfPageEffects.IsImagePage(new string('字', 100), image));
    }

    [Fact]
    public void 彩色占比_刚过阈值_判为图片页()
    {
        var boundary = (int)Math.Round(PdfPageEffects.ColoredPixelRatioThreshold * 100) + 1;

        using var image = BuildStrip(boundary, 100 - boundary);

        Assert.True(PdfPageEffects.ColoredPixelRatio(image) > PdfPageEffects.ColoredPixelRatioThreshold);
        Assert.True(PdfPageEffects.IsImagePage(new string('字', 100), image));
    }

    [Fact]
    public void 彩色占比_灰阶页_为零()
    {
        using var image = new Image<Bgra32>(10, 10);

        for (var y = 0; y < 10; y++)
        {
            for (var x = 0; x < 10; x++)
            {
                image[x, y] = new Bgra32((byte)(x * 25), (byte)(x * 25), (byte)(x * 25));
            }
        }

        Assert.Equal(0, PdfPageEffects.ColoredPixelRatio(image));
        Assert.False(PdfPageEffects.IsImagePage(new string('字', 100), image));
    }

    [Fact]
    public void 彩色占比_通道极差恰好64_不算彩色()
    {
        // 极差 = max-min；判据是严格大于 64，所以 (128,64,64) 极差 64 不算彩色。
        using var image = new Image<Bgra32>(1, 1);
        image[0, 0] = new Bgra32(128, 64, 64);

        Assert.Equal(0, PdfPageEffects.ColoredPixelRatio(image));

        image[0, 0] = new Bgra32(129, 64, 64);

        Assert.Equal(1, PdfPageEffects.ColoredPixelRatio(image));
    }

    [Fact]
    public void 图片页判定_文本足够长但整页彩色_判为图片页()
    {
        // "有文本层但是彩色照片"的混合页：只有彩色占比这一条能挡住它被反成底片。
        using var image = new Image<Bgra32>(100, 100);

        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                image[x, y] = x < 60 ? new Bgra32(220, 30, 40) : new Bgra32(255, 255, 255);
            }
        }

        Assert.Equal(0.6, PdfPageEffects.ColoredPixelRatio(image), 6);
        Assert.True(PdfPageEffects.IsImagePage(new string('字', 500), image));
    }

    [Fact]
    public void 抽样是确定性的_大图两次结果一致()
    {
        // 抽样步长由尺寸决定（确定性），不然同一页两次渲染可能得到不同的"图片页"判定。
        using var image = new Image<Bgra32>(300, 400);

        for (var y = 0; y < 400; y++)
        {
            for (var x = 0; x < 300; x++)
            {
                image[x, y] = (x + y) % 3 == 0 ? new Bgra32(255, 0, 0) : new Bgra32(255, 255, 255);
            }
        }

        var first = PdfPageEffects.ColoredPixelRatio(image);
        var second = PdfPageEffects.ColoredPixelRatio(image);

        Assert.Equal(first, second, 12);
        Assert.InRange(first, 0.3, 0.37);
    }

    // ---------------- 白底合成（2026-10-16 修"整页渲成纯黑"那个真 bug） ----------------

    /// <summary>造一段 BGRA 缓冲：每个像素依次取 (b,g,r,a)。</summary>
    private static byte[] BuildBgra(params (byte B, byte G, byte R, byte A)[] pixels)
    {
        var bytes = new byte[pixels.Length * 4];

        for (var i = 0; i < pixels.Length; i++)
        {
            bytes[i * 4] = pixels[i].B;
            bytes[(i * 4) + 1] = pixels[i].G;
            bytes[(i * 4) + 2] = pixels[i].R;
            bytes[(i * 4) + 3] = pixels[i].A;
        }

        return bytes;
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(byte[] bgra, int index) =>
        (bgra[index * 4], bgra[(index * 4) + 1], bgra[(index * 4) + 2], bgra[(index * 4) + 3]);

    [Fact]
    public void 白底合成_全透明的黑像素变成白()
    {
        // 这一条就是那个真 bug 的最小复现：pdfium 没画过的地方是 (0,0,0,0)，
        // 直接编码成 JPEG 会得到纯黑页（实测 Calibre 那份 PDF 整页 100% 黑）。
        var bgra = BuildBgra((0, 0, 0, 0));

        PdfPageEffects.FlattenOntoWhite(bgra);

        Assert.Equal((255, 255, 255, 255), PixelAt(bgra, 0));
    }

    [Fact]
    public void 白底合成_不透明黑像素保持黑()
    {
        // 关键取舍：PDF 真的画了黑色的地方（a=255）必须保持黑，
        // 否则"不分青红皂白刷白"会把正文的黑字一起抹掉。
        var bgra = BuildBgra((0, 0, 0, 255));

        PdfPageEffects.FlattenOntoWhite(bgra);

        Assert.Equal((0, 0, 0, 255), PixelAt(bgra, 0));
    }

    [Fact]
    public void 白底合成_不透明彩色像素原样保留()
    {
        var bgra = BuildBgra((30, 90, 200, 255));

        PdfPageEffects.FlattenOntoWhite(bgra);

        Assert.Equal((30, 90, 200, 255), PixelAt(bgra, 0));
    }

    [Fact]
    public void 白底合成_半透明像素按alpha混到白()
    {
        // 纯黑 50% 盖在白色上 = 127 左右（(0*128 + 255*127)/255 = 127）。
        var bgra = BuildBgra((0, 0, 0, 128));

        PdfPageEffects.FlattenOntoWhite(bgra);

        var pixel = PixelAt(bgra, 0);

        Assert.Equal(255, pixel.A);
        Assert.InRange(pixel.R, 126, 128);
        Assert.Equal(pixel.R, pixel.G);
        Assert.Equal(pixel.R, pixel.B);
    }

    [Fact]
    public void 白底合成_多像素互不影响且长度不变()
    {
        var bgra = BuildBgra((0, 0, 0, 0), (255, 255, 255, 255), (7, 8, 9, 255));

        PdfPageEffects.FlattenOntoWhite(bgra);

        Assert.Equal(12, bgra.Length);
        Assert.Equal((255, 255, 255, 255), PixelAt(bgra, 0));
        Assert.Equal((255, 255, 255, 255), PixelAt(bgra, 1));
        Assert.Equal((7, 8, 9, 255), PixelAt(bgra, 2));
    }
}
