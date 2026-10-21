using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Infrastructure.Tests.Data;

/// <summary>
/// 用户 2026-10-21：多格式共存的书籍要能切换"点阅读打开哪个格式"。
/// 这套判定（能读哪些 / MOBI·AZW3 读成 EPUB / 默认优先级链）被阅读服务、书库详情、卡片角标共用，
/// 所以单独钉住，避免三处各自漂移。
/// </summary>
public sealed class ReaderFormatSupportTests
{
    [Theory]
    [InlineData(BookFormatKind.Epub, true, BookFormatKind.Epub)]
    [InlineData(BookFormatKind.Txt, true, BookFormatKind.Txt)]
    [InlineData(BookFormatKind.Pdf, true, BookFormatKind.Pdf)]
    [InlineData(BookFormatKind.Cbz, true, BookFormatKind.Cbz)]
    [InlineData(BookFormatKind.Mobi, true, BookFormatKind.Epub)]
    [InlineData(BookFormatKind.Azw3, true, BookFormatKind.Epub)]
    [InlineData(BookFormatKind.Fb2, false, BookFormatKind.Fb2)]
    [InlineData(BookFormatKind.Unknown, false, BookFormatKind.Unknown)]
    public void 能不能读_与读到的形态(BookFormatKind kind, bool canRead, BookFormatKind readingForm)
    {
        Assert.Equal(canRead, ReaderFormatSupport.CanRead(kind));
        Assert.Equal(readingForm, ReaderFormatSupport.ReadingForm(kind));
        Assert.Equal(kind is BookFormatKind.Mobi or BookFormatKind.Azw3,
            ReaderFormatSupport.RequiresEpubConversion(kind));
    }

    [Fact]
    public void 没选过时_按_EPUB_TXT_MOBI_AZW3_PDF_CBZ_的默认链挑()
    {
        using var files = new TempFiles();

        var formats = new[]
        {
            files.Format(BookFormatKind.Cbz),
            files.Format(BookFormatKind.Pdf),
            files.Format(BookFormatKind.Azw3),
            files.Format(BookFormatKind.Mobi),
            files.Format(BookFormatKind.Txt),
            files.Format(BookFormatKind.Epub),
        };

        Assert.Equal(BookFormatKind.Epub, ReaderFormatSupport.Resolve(formats)!.Format);

        // 删掉 EPUB 文件 -> 落到 TXT（不是 MOBI，也不是 PDF）
        files.Delete(BookFormatKind.Epub);
        Assert.Equal(BookFormatKind.Txt, ReaderFormatSupport.Resolve(formats)!.Format);

        files.Delete(BookFormatKind.Txt);
        Assert.Equal(BookFormatKind.Mobi, ReaderFormatSupport.Resolve(formats)!.Format);

        files.Delete(BookFormatKind.Mobi);
        Assert.Equal(BookFormatKind.Azw3, ReaderFormatSupport.Resolve(formats)!.Format);

        files.Delete(BookFormatKind.Azw3);
        Assert.Equal(BookFormatKind.Pdf, ReaderFormatSupport.Resolve(formats)!.Format);

        files.Delete(BookFormatKind.Pdf);
        Assert.Equal(BookFormatKind.Cbz, ReaderFormatSupport.Resolve(formats)!.Format);
    }

    [Fact]
    public void 用户选过的格式优先_选的那个不可用时静默退回默认链()
    {
        using var files = new TempFiles();

        var formats = new[]
        {
            files.Format(BookFormatKind.Epub),
            files.Format(BookFormatKind.Pdf),
        };

        // 选 PDF 就用 PDF（哪怕默认链会先挑 EPUB）
        Assert.Equal(
            BookFormatKind.Pdf,
            ReaderFormatSupport.Resolve(formats, BookFormatKind.Pdf)!.Format);

        // 选了个这本书根本没有的格式：退回默认链
        Assert.Equal(
            BookFormatKind.Epub,
            ReaderFormatSupport.Resolve(formats, BookFormatKind.Mobi)!.Format);

        // 选的格式文件没了：退回默认链，而不是让这本书变成打不开
        files.Delete(BookFormatKind.Pdf);
        Assert.Equal(
            BookFormatKind.Epub,
            ReaderFormatSupport.Resolve(formats, BookFormatKind.Pdf)!.Format);
    }

    [Fact]
    public void 标了缺失或读不了的格式都不参与挑选()
    {
        using var files = new TempFiles();

        var epub = files.Format(BookFormatKind.Epub);
        epub.Missing = true;

        var formats = new[]
        {
            epub,
            files.Format(BookFormatKind.Fb2),
            files.Format(BookFormatKind.Unknown),
        };

        // FB2 / Unknown 没有阅读实现，EPUB 标了缺失 -> 一个能读的都没有
        Assert.Null(ReaderFormatSupport.Resolve(formats));
    }

    [Fact]
    public void 空列表返回空()
    {
        Assert.Null(ReaderFormatSupport.Resolve(Array.Empty<BookFormat>()));
    }

    /// <summary>造真实文件：IsUsable 会碰 File.Exists，测试必须用真文件而不是假路径。</summary>
    private sealed class TempFiles : IDisposable
    {
        private readonly Dictionary<BookFormatKind, string> _paths = new();

        public TempFiles()
        {
            Root = Path.Combine(Path.GetTempPath(), "ets-fmt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public BookFormat Format(BookFormatKind kind)
        {
            var path = Path.Combine(Root, kind + ".bin");
            File.WriteAllText(path, "x");
            _paths[kind] = path;

            return new BookFormat
            {
                BookId = Guid.NewGuid(),
                Format = kind,
                FilePath = path,
                FileSize = 1,
                AddedAt = DateTimeOffset.UtcNow,
            };
        }

        public void Delete(BookFormatKind kind)
        {
            if (_paths.TryGetValue(kind, out var path))
            {
                File.Delete(path);
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
