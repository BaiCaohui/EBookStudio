using System.Globalization;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// T5-6 第 4 步（刷新 EPUB3 的 dcterms:modified）：行为本就在 OpfMetadataEditor.Save() 里
/// （Save 会调 RefreshDctermsModified），这里把它钉住，免得以后改坏。
/// </summary>
public sealed class OpfMetadataEditorModifiedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ets-modified-" + Guid.NewGuid().ToString("N"));

    public OpfMetadataEditorModifiedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响结论
        }
    }

    [Fact]
    public void EPUB3_保存时把dcterms_modified刷成当前时间且只有一条()
    {
        var path = WriteOpf("3.0", "<meta property=\"dcterms:modified\">2020-01-01T00:00:00Z</meta>");
        var editor = new OpfMetadataEditor(path);

        editor.Read();
        editor.Save();

        var text = File.ReadAllText(path);
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        Assert.DoesNotContain("2020-01-01T00:00:00Z", text, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(text, "dcterms:modified"));
        Assert.Contains(today, text, StringComparison.Ordinal);
        Assert.Contains("T", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EPUB3_原本没有也会补上一条()
    {
        var path = WriteOpf("3.0", string.Empty);
        var editor = new OpfMetadataEditor(path);

        editor.Read();
        editor.Save();

        var text = File.ReadAllText(path);

        Assert.Equal(1, CountOf(text, "dcterms:modified"));
        Assert.Contains(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
    }

    [Fact]
    public void EPUB2_不刷新也不补()
    {
        var path = WriteOpf("2.0", string.Empty);
        var editor = new OpfMetadataEditor(path);

        editor.Read();
        editor.Save();

        Assert.DoesNotContain("dcterms:modified", File.ReadAllText(path), StringComparison.Ordinal);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private string WriteOpf(string version, string extraMeta)
    {
        var path = Path.Combine(_root, "content-" + Guid.NewGuid().ToString("N")[..6] + ".opf");
        var opf = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="{{version}}" unique-identifier="bookid">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>测试书</dc:title>
                <dc:identifier id="bookid">urn:uuid:test</dc:identifier>
                <dc:language>zh-CN</dc:language>
                {{extraMeta}}
              </metadata>
              <manifest>
                <item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine>
                <itemref idref="ch1" />
              </spine>
            </package>
            """;

        File.WriteAllText(path, opf);
        return path;
    }
}