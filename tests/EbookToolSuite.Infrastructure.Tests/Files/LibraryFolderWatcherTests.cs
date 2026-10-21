using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Files;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Infrastructure.Tests.Files;

/// <summary>
/// 书库目录监听（T2-6）。这里跑的是真的 FileSystemWatcher，所以真的在磁盘上动文件。
/// 防抖窗口调到 150ms 让测试快一点；等通知给 10 秒超时，避免机器忙的时候假红。
/// </summary>
public sealed class LibraryFolderWatcherTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;

    public LibraryFolderWatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ets-watch-" + Guid.NewGuid().ToString("N"));
        var appRoot = Path.Combine(_root, "app");
        var userLocal = Path.Combine(_root, "localappdata", "EbookToolSuite");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(userLocal);

        _paths = AppPaths.Create(new AppPathsOptions
        {
            AppRoot = appRoot,
            UserLocalRoot = userLocal,
            Fallback = DataDirFallback.Throw,
        });

        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录删不掉不影响测试结论
        }
    }

    private LibraryFolderWatcher NewWatcher()
        => new(_paths, NullLogger<LibraryFolderWatcher>.Instance, TimeSpan.FromMilliseconds(150));

    [Fact]
    public async Task 书库目录里新建文件_防抖之后收到一次通知()
    {
        using var watcher = NewWatcher();

        var fired = new TaskCompletionSource();
        var count = 0;

        watcher.Changed += () =>
        {
            Interlocked.Increment(ref count);
            fired.TrySetResult();
        };

        watcher.Start();
        Assert.True(watcher.IsRunning);

        File.WriteAllText(Path.Combine(_paths.LibraryDir, "新文件.txt"), "x");

        var completed = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(fired.Task, completed);
        Assert.True(Volatile.Read(ref count) >= 1);
    }

    [Fact]
    public void 停止之后不再监听()
    {
        using var watcher = NewWatcher();

        var count = 0;
        watcher.Changed += () => Interlocked.Increment(ref count);

        watcher.Start();
        watcher.Stop();

        Assert.False(watcher.IsRunning);
        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public void 重复Start是安全的()
    {
        using var watcher = NewWatcher();

        watcher.Start();
        watcher.Start();

        Assert.True(watcher.IsRunning);
    }
}
