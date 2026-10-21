using EbookToolSuite.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Infrastructure.Files;

/// <summary>
/// 书库目录的 FileSystemWatcher 包装（T2-6）。**只当提示用**，不是判定依据：
/// 检测到变化后等一个防抖窗口再触发一次 <see cref="Changed"/>，
/// 订阅者拿到通知后自己决定要不要跑全量扫描。
/// </summary>
/// <remarks>
/// 设计文档 7.1.3 与风险 I18 的要点都落在这里：
/// 处理 Error（重扫 + 重建 watcher）、内部缓冲调大（默认 8KB 在大目录下会溢出）、
/// 防抖（一次突发写入只等一次通知）。漏事件不影响正确性，因为兜底是手动全量扫描。
/// </remarks>
public sealed class LibraryFolderWatcher : ILibraryWatcher
{
    /// <summary>防抖窗口：一次突发写入只会等到一次通知。</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(5);

    /// <summary>内部缓冲调大（默认 8KB）。</summary>
    private const int InternalBufferSize = 64 * 1024;

    private readonly IAppPaths _paths;
    private readonly ILogger<LibraryFolderWatcher> _log;
    private readonly TimeSpan _debounce;

    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private bool _disposed;

    public LibraryFolderWatcher(IAppPaths paths, ILogger<LibraryFolderWatcher> log)
        : this(paths, log, DefaultDebounce)
    {
    }

    /// <summary>给测试用的构造：防抖窗口可以调短。</summary>
    public LibraryFolderWatcher(IAppPaths paths, ILogger<LibraryFolderWatcher> log, TimeSpan debounce)
    {
        _paths = paths;
        _log = log;
        _debounce = debounce;
    }

    public event Action? Changed;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _watcher is not null;
            }
        }
    }
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _watcher is not null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_paths.LibraryDir);

                _watcher = new FileSystemWatcher(_paths.LibraryDir)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = InternalBufferSize,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size,
                };

                _watcher.Created += (_, _) => Schedule();
                _watcher.Changed += (_, _) => Schedule();
                _watcher.Deleted += (_, _) => Schedule();
                _watcher.Renamed += (_, _) => Schedule();
                _watcher.Error += OnError;
                _watcher.EnableRaisingEvents = true;

                _log.LogInformation("书库目录监听已开启 {Dir}", _paths.LibraryDir);
            }
            catch (Exception ex)
            {
                // 监听开不起来不是致命错误：手动刷新与启动扫描照样工作
                _log.LogWarning(ex, "书库目录监听开不起来，只能靠手动刷新 {Dir}", _paths.LibraryDir);
                _watcher = null;
            }
        }
    }

    public void Stop()
    {
        FileSystemWatcher? watcher;
        Timer? timer;

        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
            timer = _timer;
            _timer = null;
        }

        if (watcher is not null)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "关闭书库目录监听时出错");
            }
        }

        timer?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Stop();
    }

    /// <summary>防抖：窗口内只留最后一次，窗口一到只触发一次。</summary>
    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _timer ??= new Timer(OnDebounceElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "处理书库目录变化通知时出错");
        }
    }

    /// <summary>
    /// 缓冲溢出之类的错误：watcher 从此失灵，必须重建，并主动提示一次让订阅者重扫兜底。
    /// </summary>
    private void OnError(object sender, ErrorEventArgs e)
    {
        _log.LogWarning(e.GetException(), "书库目录监听到错误，重建监听并提示重扫");

        Stop();
        Start();
        Schedule();
    }
}
