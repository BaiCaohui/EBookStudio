namespace EbookToolSuite.Core.Enums;

/// <summary>导入流水线的阶段，供 UI 显示细粒度状态。</summary>
public enum ImportPhase
{
    /// <summary>只读探测元数据与结构。不产生任何文件。</summary>
    Probing = 0,

    /// <summary>算快速指纹（头 64KB + 尾 64KB + 长度）。</summary>
    Hashing = 1,

    /// <summary>复制到暂存区，再移入 Data/Library 正式区。</summary>
    Copying = 2,

    /// <summary>提取封面并生成两档缩略图。</summary>
    Cover = 3,

    /// <summary>写数据库（单事务）。</summary>
    Committing = 4,

    /// <summary>收尾清理。</summary>
    Done = 5,
}