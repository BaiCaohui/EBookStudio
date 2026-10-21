using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Services.Pdf;

/// <summary>
/// 渲染好的页面位图的 **20 页 LRU** 缓存（T6-2B）。
/// </summary>
/// <remarks>
/// <para>
/// **只在内存，绝不落盘**：缓存的是一页页 JPEG（1200 长边约 200~400 KB），
/// 落盘要自己管失效（书被删、被替换、口令变了都是坑），而它本来就是"翻回上一页别重渲"的
/// 提速手段——进程退出后重新渲染一遍完全可以接受。
/// </para>
/// <para>
/// **线程安全**：pdfium 的原生调用会被多个后台任务撞上（预渲染下一页、缩略图、目录跳转），
/// 缓存必须在锁里做"查 → 挪到队首 → 淘汰队尾"这一整套，否则链表会被并发改坏。
/// 锁只圈住字典/链表操作，**不含渲染**（渲染期间不持锁，否则又把 pdfium 串行化了）。
/// </para>
/// <para>
/// **键包含主题与旋转角**：同一页在 Dark 与 Normal 下是两份不同的图，
/// 漏掉任一项都会让"切主题后翻回上一页拿到旧主题的图"。
/// </para>
/// </remarks>
public sealed class PdfPageCache
{
    /// <summary>默认容量：够覆盖"当前页 ± 几页 + 预渲染"，又不至于把几百 MB 常驻内存。</summary>
    public const int DefaultCapacity = 20;

    private readonly int _capacity;
    private readonly object _gate = new();

    /// <summary>键 → 链表节点，节点同时挂在 <see cref="_order"/> 上。</summary>
    private readonly Dictionary<CacheKey, LinkedListNode<Entry>> _map = [];

    /// <summary>最近使用顺序：队首最新，队尾最旧（淘汰从队尾走）。</summary>
    private readonly LinkedList<Entry> _order = new();

    /// <summary>建一个缓存。容量可注入，唯一的目的是让"淘汰"这条逻辑能被单测（不必塞 20 页）。</summary>
    /// <param name="capacity">最多缓存多少页，必须 ≥ 1。</param>
    public PdfPageCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _capacity = capacity;
    }

    /// <summary>容量上限。</summary>
    public int Capacity => _capacity;

    /// <summary>当前缓存了多少页。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>取一页；命中时把它挪到队首（最近使用）。</summary>
    /// <param name="path">PDF 路径，按**原样字符串**比较（上层传的路径是稳定的）。</param>
    /// <param name="pageIndex">页索引。</param>
    /// <param name="options">渲染参数；最长边/旋转角/主题都参与键。</param>
    /// <param name="image">命中时给出缓存的那一份（不可变 record，可安全共享）。</param>
    public bool TryGet(string path, int pageIndex, PdfPageRenderOptions options, out PdfPageImage image)
    {
        ArgumentNullException.ThrowIfNull(options);

        var key = CacheKey.From(path, pageIndex, options);

        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node))
            {
                image = null!;
                return false;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            image = node.Value.Image;
            return true;
        }
    }

    /// <summary>存一页；键已存在就更新值并挪到队首，超容量就淘汰队尾。</summary>
    public void Set(string path, int pageIndex, PdfPageRenderOptions options, PdfPageImage image)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(image);

        var key = CacheKey.From(path, pageIndex, options);

        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = new Entry(key, image);
                _order.Remove(existing);
                _order.AddFirst(existing);
                return;
            }

            var node = _order.AddFirst(new Entry(key, image));
            _map[key] = node;

            while (_map.Count > _capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }
        }
    }

    /// <summary>清空（切书、改口令、换主题后想强制重渲时用）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }

    /// <summary>缓存键：与"同一张图"完全一一对应。</summary>
    private readonly record struct CacheKey(
        string Path,
        int PageIndex,
        int MaxLongEdge,
        int RotationDegrees,
        PdfPageTheme Theme)
    {
        internal static CacheKey From(string path, int pageIndex, PdfPageRenderOptions options)
            => new(path, pageIndex, options.MaxLongEdge, options.RotationDegrees, options.Theme);
    }

    /// <summary>链表节点里存的键 + 值。</summary>
    private sealed record Entry(CacheKey Key, PdfPageImage Image);
}
