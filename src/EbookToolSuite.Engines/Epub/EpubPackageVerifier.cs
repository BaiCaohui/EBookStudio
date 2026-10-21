using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// 打包结果的最后一道防线（执行计划 T3-4，设计文档 6.1.5）。
/// <para>
/// 不依赖 EpubCheck：直接按 OCF 的硬性要求核对 mimetype 的位置、存储方式与内容，
/// 再核对容器描述文件与 entry 名的安全性。成本极低，每次打包后必跑。
/// </para>
/// <para>
/// 为什么不用 <c>ZipArchiveEntry.CompressedLength == Length</c> 判「没被压缩」：
/// 那只是一种巧合级别相等的近似。这里直接解析 ZIP 的本地文件头与中央目录记录，
/// 读 <c>compression method</c> 字段（0 = STORED），与 EpubCheck 看到的东西一致。
/// </para>
/// </summary>
public static class EpubPackageVerifier
{
    private const uint LocalFileHeaderSignature = 0x04034B50;
    private const uint CentralDirectorySignature = 0x02014B50;
    private const uint EndOfCentralDirectorySignature = 0x06054B50;

    /// <summary>GPF bit 3：条目后面跟 data descriptor（不可 seek 的流才会出现）。</summary>
    private const ushort DataDescriptorFlag = 0x0008;

    /// <summary>EOCD 后面可能有注释，按 ZIP 规范最多往前找 64KB + 22 字节。</summary>
    private const int MaxEndRecordBackScan = 65_536 + 22;

    private static readonly XNamespace ContainerNamespace =
        "urn:oasis:names:tc:opendocument:xmlns:container";

    /// <summary>自检；任何不合规都抛 <see cref="AppException"/>，绝不返回「部分通过」。</summary>
    public static EpubPackageVerification Verify(string epubPath)
    {
        if (string.IsNullOrWhiteSpace(epubPath))
            throw new AppException(ErrorCode.InvalidPath, "打包结果路径为空，无法自检。", nameof(epubPath));
        if (!File.Exists(epubPath))
            throw new AppException(ErrorCode.FileNotFound, "打包结果不存在，无法自检。", epubPath);

        // 1) 物理第一条必须是 mimetype：读文件开头的本地文件头，比 ZipArchive 更接近阅读器的顺序感知
        var firstLocal = ReadFirstLocalFileHeader(epubPath);
        if (!string.Equals(firstLocal.Name, EpubPackager.MimetypeEntryName, StringComparison.Ordinal))
        {
            throw new AppException(
                ErrorCode.PackageMimetypeNotFirst,
                $"打包结果不合法：mimetype 不是第一条（实际第一条是「{firstLocal.Name}」）。",
                epubPath);
        }

        if (firstLocal.CompressionMethod != 0)
        {
            throw new AppException(
                ErrorCode.PackageMimetypeNotStored,
                "打包结果不合法：mimetype 被压缩了，必须是 STORED（不压缩）。",
                $"compression method = {firstLocal.CompressionMethod}：{epubPath}");
        }

        if ((firstLocal.Flags & DataDescriptorFlag) != 0)
        {
            throw new AppException(
                ErrorCode.OutputStreamNotSeekable,
                "打包结果不合法：mimetype 带了 data descriptor（bit 3），通常是因为输出流不可 seek。",
                $"general purpose flags = 0x{firstLocal.Flags:X4}：{epubPath}");
        }

        // 2) 中央目录的第一条也必须对得上（有的工具只读中央目录）
        var firstCentral = ReadFirstCentralDirectoryRecord(epubPath);
        if (firstCentral is not null)
        {
            if (!string.Equals(firstCentral.Value.Name, EpubPackager.MimetypeEntryName, StringComparison.Ordinal))
            {
                throw new AppException(
                    ErrorCode.PackageMimetypeNotFirst,
                    $"打包结果不合法：中央目录第一条不是 mimetype（实际是「{firstCentral.Value.Name}」）。",
                    epubPath);
            }

            if (firstCentral.Value.CompressionMethod != 0)
            {
                throw new AppException(
                    ErrorCode.PackageMimetypeNotStored,
                    "打包结果不合法：中央目录里 mimetype 的压缩方式是压缩（必须是 STORED）。",
                    $"compression method = {firstCentral.Value.CompressionMethod}：{epubPath}");
            }
        }

        using var fs = File.OpenRead(epubPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        // 3) mimetype 只能有一个
        var mimetypes = zip.Entries
            .Where(entry => string.Equals(entry.FullName, EpubPackager.MimetypeEntryName, StringComparison.Ordinal))
            .ToList();

        if (mimetypes.Count == 0)
            throw new AppException(ErrorCode.PackageMimetypeNotFirst, "打包结果不合法：找不到 mimetype 条目。", epubPath);

        if (mimetypes.Count > 1)
        {
            throw new AppException(
                ErrorCode.PackageMimetypeDuplicated,
                $"打包结果不合法：mimetype 出现了 {mimetypes.Count} 次（工作区里自带的 mimetype 没被排除）。",
                epubPath);
        }

        // 4) 内容必须精确：无 BOM、无换行、一字不差
        byte[] mimetypeBytes;
        using (var stream = mimetypes[0].Open())
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            mimetypeBytes = buffer.ToArray();
        }

        if (mimetypeBytes.Length >= 3
            && mimetypeBytes[0] == 0xEF
            && mimetypeBytes[1] == 0xBB
            && mimetypeBytes[2] == 0xBF)
        {
            throw new AppException(
                ErrorCode.PackageMimetypeContentInvalid,
                "打包结果不合法：mimetype 内容带了 BOM（必须是纯粹的 application/epub+zip）。",
                epubPath);
        }

        var mimetypeText = Encoding.UTF8.GetString(mimetypeBytes);
        if (!string.Equals(mimetypeText, EpubPackager.MimetypeContent, StringComparison.Ordinal))
        {
            throw new AppException(
                ErrorCode.PackageMimetypeContentInvalid,
                "打包结果不合法：mimetype 内容不是 application/epub+zip。",
                $"实际 {mimetypeBytes.Length} 字节：{Describe(mimetypeBytes)}");
        }

        // 5) entry 名必须安全：不许反斜杠、不许 Zip Slip、不许盘符
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.Contains('\\'))
            {
                throw new AppException(
                    ErrorCode.UnsafeArchiveEntry,
                    "打包结果不合法：压缩包有非法路径（条目名里出现反斜杠）。",
                    entry.FullName);
            }

            if (EpubPackageIndex.IsUnsafeEntryName(entry.FullName))
            {
                throw new AppException(
                    ErrorCode.UnsafeArchiveEntry,
                    "打包结果不合法：压缩包有非法路径。",
                    entry.FullName);
            }
        }

        // 6) container.xml 必须在，且它指向的 OPF 真的在包里
        var container = zip.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName, EpubPackager.ContainerEntryName, StringComparison.Ordinal));

        if (container is null)
            throw new AppException(ErrorCode.EpubInvalid, "打包结果不合法：缺少 META-INF/container.xml。", epubPath);

        string? opfPath;
        try
        {
            using var containerStream = container.Open();
            var document = XDocument.Load(containerStream);
            opfPath = document.Root?
                .Element(ContainerNamespace + "rootfiles")?
                .Element(ContainerNamespace + "rootfile")?
                .Attribute("full-path")?
                .Value;
        }
        catch (XmlException ex)
        {
            throw new AppException(
                ErrorCode.EpubInvalid,
                "打包结果不合法：META-INF/container.xml 解析失败。",
                ex.Message,
                ex);
        }

        if (string.IsNullOrWhiteSpace(opfPath))
        {
            throw new AppException(
                ErrorCode.EpubInvalid,
                "打包结果不合法：container.xml 里没有 rootfile@full-path。",
                epubPath);
        }

        if (EpubPackageIndex.IsUnsafeEntryName(opfPath))
        {
            throw new AppException(
                ErrorCode.UnsafeArchiveEntry,
                "打包结果不合法：压缩包有非法路径（OPF 路径）。",
                opfPath);
        }

        var normalizedOpfPath = EpubPackageIndex.NormalizeEntryPath(opfPath);
        if (zip.Entries.All(entry => !string.Equals(entry.FullName, normalizedOpfPath, StringComparison.Ordinal)))
        {
            throw new AppException(
                ErrorCode.EpubInvalid,
                $"打包结果不合法：container.xml 指向的 OPF 不在包里（{opfPath}）。",
                epubPath);
        }

        return new EpubPackageVerification(
            zip.Entries.Count,
            mimetypeBytes.Length,
            new FileInfo(epubPath).Length);
    }

    /// <summary>异步壳子，方便调用方直接 await。</summary>
    public static Task<EpubPackageVerification> VerifyAsync(string epubPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Verify(epubPath));
    }

    /// <summary>读文件开头第一个本地文件头的 名称 / 压缩方式 / GPF。</summary>
    private static (string Name, int CompressionMethod, int Flags) ReadFirstLocalFileHeader(string epubPath)
    {
        using var fs = File.OpenRead(epubPath);
        if (fs.Length < 30)
        {
            throw new AppException(
                ErrorCode.EpubInvalid,
                "打包结果不合法：文件太小，不可能是有效的 EPUB。",
                $"{fs.Length} 字节：{epubPath}");
        }

        Span<byte> header = stackalloc byte[30];
        fs.ReadExactly(header);

        if (BitConverter.ToUInt32(header[..4]) != LocalFileHeaderSignature)
        {
            throw new AppException(
                ErrorCode.PackageMimetypeNotFirst,
                "打包结果不合法：压缩包第一条不是 mimetype。",
                $"文件开头不是 ZIP 本地文件头：{epubPath}");
        }

        var flags = BitConverter.ToUInt16(header[6..8]);
        var method = BitConverter.ToUInt16(header[8..10]);
        var nameLength = BitConverter.ToUInt16(header[26..28]);
        if (nameLength == 0)
            throw new AppException(ErrorCode.PackageMimetypeNotFirst, "打包结果不合法：第一条 entry 没有名字。", epubPath);

        var nameBytes = new byte[nameLength];
        fs.ReadExactly(nameBytes);
        return (Encoding.UTF8.GetString(nameBytes), method, flags);
    }

    /// <summary>
    /// 读中央目录的第一条记录。返回 null 表示是 ZIP64（&gt; 4GB 或 &gt; 65535 条），
    /// 这套结构这里不解析，交给 EpubCheck 兜底。
    /// </summary>
    private static (string Name, int CompressionMethod, int EntryCount)? ReadFirstCentralDirectoryRecord(string epubPath)
    {
        using var fs = File.OpenRead(epubPath);

        var tailLength = (int)Math.Min(fs.Length, MaxEndRecordBackScan);
        var tail = new byte[tailLength];
        fs.Seek(fs.Length - tailLength, SeekOrigin.Begin);
        fs.ReadExactly(tail);

        var endRecordIndex = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
            {
                endRecordIndex = i;
                break;
            }
        }

        if (endRecordIndex < 0)
            throw new AppException(ErrorCode.EpubInvalid, "打包结果不合法：找不到 ZIP 中央目录结束记录。", epubPath);

        var entryCount = BitConverter.ToUInt16(tail, endRecordIndex + 10);
        var centralOffset = BitConverter.ToUInt32(tail, endRecordIndex + 16);

        if (entryCount == ushort.MaxValue || centralOffset == uint.MaxValue)
            return null;

        try
        {
            var header = new byte[46];
            fs.Seek(centralOffset, SeekOrigin.Begin);
            fs.ReadExactly(header);

            if (BitConverter.ToUInt32(header, 0) != CentralDirectorySignature)
                throw new AppException(ErrorCode.EpubInvalid, "打包结果不合法：ZIP 中央目录结构损坏。", epubPath);

            var method = BitConverter.ToUInt16(header, 10);
            var nameLength = BitConverter.ToUInt16(header, 28);
            var nameBytes = new byte[nameLength];
            fs.ReadExactly(nameBytes);

            return (Encoding.UTF8.GetString(nameBytes), method, entryCount);
        }
        catch (IOException ex)
        {
            throw new AppException(
                ErrorCode.EpubInvalid,
                "打包结果不合法：ZIP 结构损坏或被截断。",
                ex.Message,
                ex);
        }
    }

    private static string Describe(byte[] bytes)
    {
        var preview = string.Join(' ', bytes.Take(32).Select(b => b.ToString("X2")));
        return preview.Length == 0 ? "(空)" : preview;
    }
}

/// <summary>自检结果：条目数、mimetype 字节数、成品大小。</summary>
public sealed record EpubPackageVerification(int EntryCount, int MimetypeLength, long ArchiveBytes);
