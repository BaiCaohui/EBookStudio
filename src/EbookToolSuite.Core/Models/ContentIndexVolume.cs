namespace EbookToolSuite.Core.Models;

/// <summary>索引体积（T6-4 判据 8：索引体积记录在案，用于磁盘预算）。</summary>
public sealed record ContentIndexVolume
{
    /// <summary>切片条数。</summary>
    public long ChunkCount { get; init; }

    /// <summary>content_chunk 里正文的字节数（UTF-8）。</summary>
    public long BodyBytes { get; init; }

    /// <summary>FTS5 倒排索引占的字节数（含 trigram 的 3-5 倍膨胀；用 dbstat 量，拿不到时退回 0）。</summary>
    public long FtsBytes { get; init; }

    public long TotalBytes => BodyBytes + FtsBytes;
}
