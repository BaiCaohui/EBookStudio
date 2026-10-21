using System.Text.Json.Nodes;
using EbookToolSuite.Core.Interfaces;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// TXT 的"每本书一个编码"（用户 2026-10-20 要求）：
/// 阅读器里的编码选择器与 TXT 全文编辑器里的编码选择器**共用同一个 Config 键**，
/// 任一边改了编码，另一边立即跟着换（<see cref="Changed"/>）。
///
/// 键里带书 Id，所以**只对这一本书生效**：这本书选了 GBK，别的 TXT 书仍走默认（自动识别）。
/// </summary>
public static class TxtBookEncoding
{
    /// <summary>Config.json 里的前缀（沿用 T4-5 的键，老配置不用迁移）。</summary>
    public const string ConfigKeyPrefix = "reader.text.encoding.";

    /// <summary>sample的编码被改了（参数里带书 Id，订阅方自己过滤）。</summary>
    public static event EventHandler<TxtBookEncodingChangedEventArgs>? Changed;

    public static string ConfigKey(Guid bookId) => ConfigKeyPrefix + bookId.ToString("D");

    /// <summary>这本书手选过的编码；null = 没选过，按默认（自动识别）。</summary>
    public static string? Get(IConfigStore? config, Guid bookId)
        => config?.GetString(ConfigKey(bookId), null);

    /// <summary>记下这本书的编码（先改内存再落盘，与阅读器 T4-5 的写法一致）。</summary>
    public static async Task SetAsync(IConfigStore? config, Guid bookId, string encodingName)
    {
        if (config is null)
        {
            return;
        }

        await config.SetAsync(ConfigKey(bookId), JsonValue.Create(encodingName)).ConfigureAwait(true);
        await config.SaveAsync().ConfigureAwait(true);
    }

    /// <summary>广播：这张书的编码变了（阅读器 / 编辑器都订阅它）。</summary>
    public static void Publish(Guid bookId, string encodingName)
        => Changed?.Invoke(null, new TxtBookEncodingChangedEventArgs(bookId, encodingName));
}

/// <summary>编码变更事件参数：哪本书、改成了什么编码。</summary>
public sealed class TxtBookEncodingChangedEventArgs : EventArgs
{
    public TxtBookEncodingChangedEventArgs(Guid bookId, string encodingName)
    {
        BookId = bookId;
        EncodingName = encodingName;
    }

    public Guid BookId { get; }

    public string EncodingName { get; }
}
