using System.Buffers.Binary;
using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Engines.Kindle;

namespace EbookToolSuite.Engines.Tests.Kindle;

/// <summary>
/// MOBI / AZW3 导入体检（T6-1 前置）：该放行的放行、结构不对的拦住、
/// DRM 按设计文档 6.1.9（drm_offset/drm_count 或 EXTH 201/202）拦住。
/// </summary>
public sealed class MobiProbeTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var file in _files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void 合法MOBI_通过()
    {
        MobiProbe.Validate(NewFile(BuildMobi()));
    }

    [Fact]
    public void EXTH存在但没有DRM记录_通过()
    {
        MobiProbe.Validate(NewFile(BuildMobi(exthTypes: new uint[] { 100, 503 })));
    }

    [Fact]
    public void 没有BOOKMOBI头的文件_判为无效()
    {
        var ex = Assert.Throws<AppException>(() => MobiProbe.Validate(NewFile(BuildMobi(magic: false))));
        Assert.Equal(ErrorCode.EpubInvalid, ex.Code);
    }

    [Fact]
    public void 零内容记录_判为无效()
    {
        var ex = Assert.Throws<AppException>(() => MobiProbe.Validate(NewFile(BuildMobi(recordCount: 0))));
        Assert.Equal(ErrorCode.EpubInvalid, ex.Code);
    }

    [Fact]
    public void drm_offset有值_判为DRM()
    {
        var ex = Assert.Throws<AppException>(() => MobiProbe.Validate(
            NewFile(BuildMobi(drmOffset: 1024, drmCount: 1))));
        Assert.Equal(ErrorCode.DrmDetected, ex.Code);
    }

    [Fact]
    public void EXTH记录201_判为DRM()
    {
        var ex = Assert.Throws<AppException>(() => MobiProbe.Validate(
            NewFile(BuildMobi(exthTypes: new uint[] { 100, 201 }))));
        Assert.Equal(ErrorCode.DrmDetected, ex.Code);
    }

    [Fact]
    public void EXTH记录202_判为DRM()
    {
        var ex = Assert.Throws<AppException>(() => MobiProbe.Validate(
            NewFile(BuildMobi(exthTypes: new uint[] { 202 }))));
        Assert.Equal(ErrorCode.DrmDetected, ex.Code);
    }

    [Fact]
    public void 文件不存在_抛FileNotFound()
    {
        var ex = Assert.Throws<AppException>(() => MobiProbe.Validate(
            Path.Combine(Path.GetTempPath(), "mobi-probe-missing-" + Guid.NewGuid().ToString("N") + ".mobi")));
        Assert.Equal(ErrorCode.FileNotFound, ex.Code);
    }

    [Fact]
    public void 太短的文件_判为无效()
    {
        var ex = Assert.Throws<AppException>(() => MobiProbe.Validate(NewFile(new byte[64])));
        Assert.Equal(ErrorCode.EpubInvalid, ex.Code);
    }

    private string NewFile(byte[] bytes)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "mobi-probe-" + Guid.NewGuid().ToString("N") + ".mobi");
        File.WriteAllBytes(path, bytes);
        _files.Add(path);
        return path;
    }

    /// <summary>造一个最小的合法 MOBI：PalmDB 头 + 一条记录（PalmDOC 头 + MOBI 头 + 可选 EXTH）。</summary>
    private static byte[] BuildMobi(
        uint drmOffset = 0xFFFFFFFFu,
        uint drmCount = 0,
        uint[]? exthTypes = null,
        bool magic = true,
        ushort recordCount = 1)
    {
        const int record0Offset = 78 + 8 + 2;   // 头 + 一条记录信息 + 结尾 2 字节
        const int mobiHeaderLength = 232;

        var exthPresent = exthTypes is { Length: > 0 };
        var exthBytes = exthPresent ? (uint)(12 + exthTypes!.Length * 8) : 0u;

        var record = new byte[16 + mobiHeaderLength + exthBytes];
        var m = 16;   // PalmDOC 头占前 16 字节

        Encoding.ASCII.GetBytes("MOBI").CopyTo(record, m);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(m + 4), mobiHeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(m + 8), 2);        // mobi type = Mobipocket Book
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(m + 12), 65001);   // UTF-8
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(m + 116), exthPresent ? 0x40u : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(m + 128), drmOffset);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(m + 132), drmCount);

        if (exthPresent)
        {
            var e = m + mobiHeaderLength;
            Encoding.ASCII.GetBytes("EXTH").CopyTo(record, e);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(e + 4), exthBytes);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(e + 8), (uint)exthTypes!.Length);

            var cursor = e + 12;
            foreach (var type in exthTypes)
            {
                BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(cursor), type);
                BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(cursor + 4), 8);
                cursor += 8;
            }
        }

        var header = new byte[record0Offset];
        Encoding.ASCII.GetBytes(magic ? "BOOK" : "JUNK").CopyTo(header, 60);
        Encoding.ASCII.GetBytes(magic ? "MOBI" : "JUNK").CopyTo(header, 64);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(76), recordCount);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(78), record0Offset);

        var file = new byte[record0Offset + record.Length];
        header.CopyTo(file, 0);
        record.CopyTo(file, record0Offset);
        return file;
    }
}