namespace EbookToolSuite.Core.Models;

/// <summary>索引进度（T6-4：界面显示「索引中 N/M」）。</summary>
public sealed record ContentIndexProgress
{
    public bool Running { get; init; }

    /// <summary>这一轮要索引的总本数（开始时的剩余量）。</summary>
    public int Total { get; init; }

    /// <summary>这一轮已完成的本数。</summary>
    public int Done { get; init; }

    /// <summary>还没轮到 / 中断后待续的本数。</summary>
    public int Pending { get; init; }

    public int Failed { get; init; }

    /// <summary>正在索引的书名（没有就 null）。</summary>
    public string? CurrentTitle { get; init; }
}
