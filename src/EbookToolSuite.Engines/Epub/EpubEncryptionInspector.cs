using System.IO.Compression;
using System.Xml.Linq;

namespace EbookToolSuite.Engines.Epub;

/// <summary>encryption.xml 的体检结果。</summary>
/// <param name="HasEncryptionFile">包里有没有 META-INF/encryption.xml。</param>
/// <param name="IsDrm">真 DRM（覆盖了非字体资源，或算法不是字体混淆）。</param>
/// <param name="FontObfuscatedOnly">只有字体混淆，正文没有任何加密。</param>
/// <param name="EncryptedResourceCount">EncryptedData 条数。</param>
/// <param name="Algorithms">出现过的算法（诊断用）。</param>
/// <param name="DrmAlgorithm">判定为 DRM 时用到的第一个算法。</param>
public sealed record EpubEncryptionReport(
    bool HasEncryptionFile,
    bool IsDrm,
    bool FontObfuscatedOnly,
    int EncryptedResourceCount,
    IReadOnlyList<string> Algorithms,
    string? DrmAlgorithm);

/// <summary>
/// META-INF/encryption.xml 的分类器。规格见设计文档 6.1.9。
/// </summary>
/// <remarks>
/// 【为什么不能"有 encryption.xml 就判 DRM"】
/// external 等来源的书大量使用 IDPF 字体混淆：只把字体文件的**前 1040 字节** XOR 掉，
/// 正文一个字都没加密，各家阅读器都能正常打开。把它一律当 DRM 拒收，会把正常书挡在门外
/// （2026-10-02 本机实测：I love Subject 的 encryption.xml 只有 3 条 IDPF 字体混淆记录，
/// 覆盖 3 个 TTF，却被判成"带 DRM 加密，无法导入"）。风险登记册 R16 早就写过这一条。
/// </remarks>
public static class EpubEncryptionInspector
{
    private const string IdpfObfuscation = "http://www.idpf.org/2008/embedding";
    private const string AdobeObfuscation = "http://ns.adobe.com/pdf/enc#RC";
    private const string EncryptionEntryName = "META-INF/encryption.xml";
    private const int MaxEncryptionXmlBytes = 4 * 1024 * 1024;

    private static readonly string[] FontExtensions = [".ttf", ".otf", ".woff", ".woff2", ".ttc"];

    private static readonly EpubEncryptionReport None = new(false, false, false, 0, [], null);

    public static EpubEncryptionReport Inspect(string epubPath)
    {
        try
        {
            using var fs = new FileStream(
                epubPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

            var entry = zip.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, EncryptionEntryName, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                return None;
            }

            if (entry.Length > MaxEncryptionXmlBytes)
            {
                // 大得离谱：宁可当 DRM 拒掉，也不要在这里做无界解析
                return new EpubEncryptionReport(
                    true, true, false, 0, ["(encryption.xml 过大)"], "(unknown)");
            }

            using var stream = entry.Open();
            var doc = XDocument.Load(stream);

            var algorithms = new List<string>();
            var drmAlgorithm = (string?)null;
            var count = 0;
            var isDrm = false;

            foreach (var data in doc.Descendants().Where(e => e.Name.LocalName == "EncryptedData"))
            {
                count++;

                var algorithm = data.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "EncryptionMethod")
                    ?.Attribute("Algorithm")?.Value ?? string.Empty;

                var uri = data.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "CipherReference")
                    ?.Attribute("URI")?.Value ?? string.Empty;

                if (!algorithms.Contains(algorithm))
                {
                    algorithms.Add(algorithm);
                }

                // 只有"算法是 IDPF/Adobe 混淆"**且**"覆盖的是字体资源"才算字体混淆。
                // 算法对但覆盖了正文（比如指向 xhtml）一样按 DRM 处理。
                var obfuscationAlgorithm = algorithm is IdpfObfuscation or AdobeObfuscation;
                if (!obfuscationAlgorithm || !LooksLikeFont(uri))
                {
                    isDrm = true;
                    drmAlgorithm ??= string.IsNullOrEmpty(algorithm) ? "(未声明算法)" : algorithm;
                }
            }

            if (count == 0)
            {
                // 空的 encryption.xml：什么都没加密，按正常书处理
                return new EpubEncryptionReport(true, false, false, 0, [], null);
            }

            return new EpubEncryptionReport(true, isDrm, !isDrm, count, algorithms, drmAlgorithm);
        }
        catch (Exception)
        {
            // 读不出来就当没有：真是 DRM 的话，读正文时也会暴露
            return None;
        }
    }

    /// <summary>URI 的扩展名像不像字体。忽略 #fragment 与查询串。</summary>
    private static bool LooksLikeFont(string uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return false;
        }

        var path = uri.Split('#')[0];
        return FontExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}