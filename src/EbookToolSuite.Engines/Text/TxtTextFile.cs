using System.Text;
using EbookToolSuite.Core.IO;

namespace EbookToolSuite.Engines.Text;

/// <summary>
/// 整本 TXT 的原文 + 它的"落盘身份"（路径 / 编码 / 有没有 BOM）。
/// 【为什么把编码一起带着】编辑保存必须**原样写回**：用户改一段文字，
/// 不该顺手把 UTF-8 弄成 GBK、也不该把开头的 BOM 弄丢（丢了阅读器下次会识别错）。
/// </summary>
/// <param name="Path">源文件路径（保存写回同一个文件）。</param>
/// <param name="Text">解码后的全文（已跳过 BOM）。</param>
/// <param name="Encoding">写回时用的编码实例。</param>
/// <param name="EncodingName">界面上的标准编码名（UTF-8 / GBK …）。</param>
/// <param name="HasBom">原文件是否带 BOM（保存时照原样写回 / 不写回）。</param>
public sealed record TxtTextDocument(
    string Path,
    string Text,
    Encoding Encoding,
    string EncodingName,
    bool HasBom);

/// <summary>
/// 整本 TXT 的读 / 写（用户 2026-10-20 要求：TXT 也要能像记事本一样直接改**全文**）。
///
/// 读：编码优先用"阅读器正在用的那个"（<paramref name="encodingName"/>），
///     没给就自动识别；BOM 字节跳过，字符下标与阅读清单里的 <c>TextStart</c> 对齐。
/// 写：原编码 + 原 BOM + 原换行符写回（换行符原样保留在 Text 里，这里不做归一化），
///     并且走 <see cref="AtomicFile"/>：先写同目录 <c>.part</c> 再原子改名，
///     断电 / 磁盘满也不会留下半截 TXT。
/// </summary>
public static class TxtTextFile
{
    /// <summary>读出整本 TXT。</summary>
    public static TxtTextDocument Load(string path, string? encodingName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var detected = TextEncodingDetector.Detect(path);
        var overridden = TextEncodingSupport.Resolve(encodingName);
        var encoding = overridden ?? detected.Encoding;
        var name = overridden is null ? detected.Name : TextEncodingSupport.Normalize(encodingName);
        var bomLength = TextEncodingDetector.BomLengthFor(path, encoding);

        var bytes = File.ReadAllBytes(path);
        var skip = Math.Min(Math.Max(0, bomLength), bytes.Length);
        var text = encoding.GetString(bytes, skip, bytes.Length - skip);

        return new TxtTextDocument(path, text, encoding, name, bomLength > 0);
    }

    /// <summary>把全文写回原文件（编码 / BOM 照原样）。调用方负责把未保存提示处理好。</summary>
    public static void Save(TxtTextDocument document, string text)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(text);

        var bom = BomBytes(document.Encoding, document.HasBom);
        var body = document.Encoding.GetBytes(text);
        var bytes = new byte[bom.Length + body.Length];

        if (bom.Length > 0)
        {
            Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
        }

        Buffer.BlockCopy(body, 0, bytes, bom.Length, body.Length);

        AtomicFile.WriteAllBytes(document.Path, bytes);
    }

    /// <summary>
    /// BOM 字节：
    /// · 编码本身"天生带 BOM"的（UTF-8-BOM / UTF-16 / UTF-32）写它自己的；
    /// · 其它编码跟随原文件（原来有就保留，原来没有就不加）。
    /// </summary>
    private static byte[] BomBytes(Encoding encoding, bool hasBom)
    {
        var preamble = encoding.GetPreamble();

        if (preamble.Length > 0)
        {
            return preamble;
        }

        if (!hasBom)
        {
            return Array.Empty<byte>();
        }

        return encoding.CodePage switch
        {
            65001 => new byte[] { 0xEF, 0xBB, 0xBF }, // UTF-8
            1200 => new byte[] { 0xFF, 0xFE },        // UTF-16LE
            1201 => new byte[] { 0xFE, 0xFF },        // UTF-16BE
            _ => Array.Empty<byte>(),
        };
    }
}
