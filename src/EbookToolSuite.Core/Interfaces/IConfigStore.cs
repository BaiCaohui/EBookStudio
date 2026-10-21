using System.Text.Json.Nodes;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// Config.json 的唯一读写入口。规格见设计文档 9.10.3。
/// </summary>
/// <remarks>
/// 核心约束：**绝不丢字段**。实现必须把文件当成 JsonObject 按路径增删改，
/// 禁止用 POCO 整体反序列化再整体序列化  那会静默丢掉程序还没实现的设置项，
/// 而且要等到某天发现用户设置被清空才会暴露。
/// </remarks>
public interface IConfigStore
{
    /// <summary>启动时调一次：把 Config.json 读进内存。之后 Get 全是内存操作，同步且很快。</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>按点分路径读（如 "ui.windowStates.main.left"）。路径不存在返回 null。</summary>
    /// <remarks>返回的是树上那个活节点，调用方只读；要搬走请先 DeepClone。</remarks>
    JsonNode? Get(string dottedPath);

    string? GetString(string dottedPath, string? fallback = null);

    bool GetBool(string dottedPath, bool fallback);

    double GetDouble(string dottedPath, double fallback);

    /// <summary>按点分路径写；中间层级不存在时逐级创建。只改内存，不落盘。</summary>
    Task SetAsync(string dottedPath, JsonNode? value, CancellationToken ct = default);

    /// <summary>原子落盘：写 .tmp 再同卷改名。整份文档一次写完。</summary>
    Task SaveAsync(CancellationToken ct = default);
}