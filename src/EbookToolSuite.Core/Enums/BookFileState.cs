namespace EbookToolSuite.Core.Enums;

/// <summary>
/// 书库文件在磁盘上的状态（T2-6 文件监控与缺失检测）。
/// </summary>
/// <remarks>
/// 数值大小有含义：<see cref="Missing"/> 比 <see cref="ExternallyModified"/> 严重，
/// 一本书有多个格式时取"数值最小的那个"当整本书的状态。
/// </remarks>
public enum BookFileState
{
    /// <summary>文件在，大小与修改时间跟库里记录一致。</summary>
    Normal = 0,

    /// <summary>文件不在了（记录还在库里，磁盘上没有）。</summary>
    Missing = 1,

    /// <summary>文件在，但大小或修改时间变了：被外部改过。</summary>
    ExternallyModified = 2,
}
