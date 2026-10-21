namespace EbookToolSuite.Core.Abstractions;

/// <summary>重复检测的参数。</summary>
public sealed class DuplicateScanOptions
{
    /// <summary>只对候选组算 content_hash（懒计算）。关掉只做快速分组，用于排障。</summary>
    public bool ComputeContentHashes { get; init; } = true;

    /// <summary>组内成员数超过它就整组跳过（防病态数据，例如几百本同名书）。</summary>
    public int MaxGroupSize { get; init; } = 20;

    /// <summary>算 content_hash 的并发度上限。</summary>
    public int MaxParallelism { get; init; } = 2;

    /// <summary>整组算哈希的总时限，超时就报告「未复核」而不是让界面干等。</summary>
    public TimeSpan HashBudget { get; init; } = TimeSpan.FromSeconds(8);

    public static DuplicateScanOptions Default { get; } = new();
}

