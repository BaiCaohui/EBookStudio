using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace EbookToolSuite.App;

/// <summary>
/// 长任务反馈（T7-3）：统一"状态栏文案 + 等待光标"。
///
/// 两个刻意的设计：
///   1) 光标延迟 500 ms 才出现——短操作一闪而过反而让人以为卡了；
///   2) 支持嵌套（Begin 两次只要 Dispose 两次），导入里还套着扫描也不会把光标提前收掉。
/// </summary>
public static class BusyScope
{
    private static readonly DispatcherTimer DelayTimer = new(DispatcherPriority.Normal)
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };

    private static int _depth;

    static BusyScope()
    {
        DelayTimer.Tick += (_, _) =>
        {
            DelayTimer.Stop();
            if (_depth > 0)
            {
                Mouse.OverrideCursor = Cursors.Wait;
            }
        };
    }

    /// <summary>主窗口注册：状态栏文案的出口。传 null 表示恢复"就绪"。</summary>
    public static Action<string?>? StatusSink { get; set; }

    /// <summary>进入一个忙碌区间；用 <c>using</c> 包住即可。</summary>
    public static IDisposable Begin(string statusText = "处理中…")
    {
        if (_depth == 0)
        {
            StatusSink?.Invoke(statusText);
            DelayTimer.Start();
        }

        _depth++;
        return new Scope();
    }

    /// <summary>忙碌区间结束。</summary>
    private static void End()
    {
        _depth = Math.Max(0, _depth - 1);
        if (_depth > 0)
        {
            return;
        }

        DelayTimer.Stop();
        Mouse.OverrideCursor = null;
        StatusSink?.Invoke(null);
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            End();
        }
    }
}
