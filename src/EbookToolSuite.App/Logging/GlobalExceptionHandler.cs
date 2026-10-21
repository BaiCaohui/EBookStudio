using System.IO;
using System.Windows;
using System.Windows.Threading;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Infrastructure.Logging;
using Serilog;

namespace EbookToolSuite.App.Logging;

/// <summary>
/// 三处全局异常兜底（设计文档 3.7）。**缺一个就有一类崩溃查不到**：
///   1. DispatcherUnhandledException      -- UI 线程
///   2. AppDomain.UnhandledException       -- 后台线程（进程即将终止）
///   3. TaskScheduler.UnobservedTaskException -- 未观察的 Task（最容易被忽略）
/// </summary>
public sealed class GlobalExceptionHandler
{
    private readonly ILogger _log = Log.ForContext<GlobalExceptionHandler>();
    private readonly string _logDirectory;

    public GlobalExceptionHandler(string logDirectory)
        => _logDirectory = logDirectory;

    /// <summary>冒烟测试用：置 true 时不弹窗，只记日志。</summary>
    public bool SuppressDialogs { get; set; }

    public int UiExceptionCount { get; private set; }

    public int BackgroundExceptionCount { get; private set; }

    public int UnobservedTaskExceptionCount { get; private set; }

    public void Hook(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;

        _log.Information(
            "全局异常处理器已挂接（日志目录 {LogDir}，内存缓冲 {RingCount} 条）",
            _logDirectory,
            LogRingBuffer.Instance.Count);
    }

    // ---------- 1. UI 线程 ----------

    private void OnDispatcherUnhandled(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        UiExceptionCount++;
        _log.Error(e.Exception, "UI 线程未处理异常");

        if (!SuppressDialogs)
        {
            ShowDialog("界面出现异常", e.Exception);
        }

        // 关键：不设 Handled 的话进程会直接死，用户看不到任何提示，
        // 日志也可能来不及落盘。
        e.Handled = true;
    }

    // ---------- 2. 后台线程 ----------

    private void OnAppDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        BackgroundExceptionCount++;
        var ex = e.ExceptionObject as Exception;

        _log.Fatal(
            ex,
            "后台线程未处理异常，进程即将终止（IsTerminating={Terminating}）",
            e.IsTerminating);

        // 这一处来不及也不能弹窗（CLR 正在终止进程），只能尽力把日志刷出去。
        Log.CloseAndFlush();
    }

    // ---------- 3. 未观察的 Task ----------

    private void OnUnobservedTask(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        UnobservedTaskExceptionCount++;
        _log.Error(e.Exception, "未观察的 Task 异常");

        // 不 SetObserved 的话，.NET 在某些配置下会把它升级成进程终止
        e.SetObserved();
    }

    // ---------- 弹窗 ----------

    private void ShowDialog(string title, Exception ex)
    {
        try
        {
            /* T7-2 判据 2：界面上只出现中文。
               异常类型名（System.IO.IOException）、英文 Message、堆栈一律只进日志，
               弹窗里给的是错误码 + 人话 + 日志位置。 */
            var isApp = ex is AppException;
            var info = ErrorCatalog.Get(isApp ? ((AppException)ex).Code : ErrorCode.Unknown);

            var text = string.Join(
                Environment.NewLine,
                "发生了未预期的错误，但程序会继续运行。",
                string.Empty,
                isApp ? ((AppException)ex).UserMessage : info.Message,
                isApp ? string.Empty : info.Action,
                string.Empty,
                "错误码：" + info.EtsCode,
                "完整技术细节已写入日志：",
                _logDirectory,
                string.Empty,
                "可以从「帮助 - 打开日志目录」找到它。");

            AppDialog.Show(
                text,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception dialogFailure)
        {
            // 连弹窗都失败就只能写日志了
            _log.Warning(dialogFailure, "错误提示窗显示失败");
        }
    }
}