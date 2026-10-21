using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>一本书的正文索引状态（T6-4，落 content_index_state 表）。</summary>
public sealed record ContentIndexState
{
    public required Guid BookId { get; init; }

    public required ContentIndexStatus Status { get; init; }

    public int ChunkCount { get; init; }

    /// <summary>失败原因（面向日志/排障，不直接给用户看）。</summary>
    public string? Error { get; init; }

    public long UpdatedAt { get; init; }
}
