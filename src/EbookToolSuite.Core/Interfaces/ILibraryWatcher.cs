namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 书库目录变化提示（T2-6）。**只当提示用**，不是判定依据：
/// FileSystemWatcher 在网络盘/大目录下不可靠，漏事件、丢事件都可能发生。
/// 真正的判定一律以全量扫描（<see cref="ILibraryScanService"/>）为准。
/// </summary>
public interface ILibraryWatcher : IDisposable
{
    /// <summary>监听中。目录不存在时会先建出来。</summary>
    bool IsRunning { get; }

    /// <summary>开始监听。重复调用是安全的（已经在跑就直接返回）。</summary>
    void Start();

    void Stop();

    /// <summary>
    /// 防抖之后的变化通知。**在后台线程触发**，订阅者自己切 UI 线程。
    /// 一次突发写入（比如批量导入 200 本）只会等到一次通知。
    /// </summary>
    event Action? Changed;
}
