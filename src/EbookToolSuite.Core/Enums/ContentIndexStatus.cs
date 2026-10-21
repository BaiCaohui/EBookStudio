namespace EbookToolSuite.Core.Enums;

/// <summary>
/// 一本书正文索引的状态（T6-4）。落库时存枚举名字符串，便于直接读库排障。
/// </summary>
public enum ContentIndexStatus
{
    /// <summary>还没索引（导入后、或上次中断留下的）。重启时会捡起来续建。</summary>
    Pending,

    /// <summary>正在索引。进程崩在这一步也没关系：重启时按 Pending 重来。</summary>
    Indexing,

    /// <summary>索引完成。</summary>
    Done,

    /// <summary>索引失败（文件损坏、抽取抛异常等）。不自动重试，避免每次启动都卡在同一本。</summary>
    Failed,

    /// <summary>这个格式不索引正文（漫画 / MOBI / AZW3）。不算失败，也不该反复重试。</summary>
    Unsupported,
}
