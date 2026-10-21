using System.IO;
using System.Text;
using EbookToolSuite.Core.Interfaces;
using Serilog;
using Serilog.Events;

namespace EbookToolSuite.Infrastructure.Logging;

/// <summary>
/// Serilog 配置（设计文档 3.8）。
///
/// 两条硬要求：
///   1. 日志必须写在 Data/Logs/ 内  否则绿色化验收直接失败；
///   2. 编码必须是 UTF-8 **无 BOM**  用记事本打开不能有乱码。
/// </summary>
public static class LoggingSetup
{
    public const int RetainedFileCount = 14;

    public const long FileSizeLimitBytes = 20L * 1024 * 1024;

    public const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>日志文件路径模板（滚动后的实际文件名形如 app-20261001.log）。</summary>
    public static string FilePathTemplate(IAppPaths paths)
        => Path.Combine(paths.LogsDir, "app-.log");

    /// <summary>当天日志文件的完整路径。滚动按天，所以文件名是 app-yyyyMMdd.log。</summary>
    public static string CurrentFilePath(IAppPaths paths, DateTimeOffset now)
        => Path.Combine(paths.LogsDir, "app-" + now.ToString("yyyyMMdd") + ".log");

    public static LoggerConfiguration Create(IAppPaths paths)
        => Create(paths, LogEventLevel.Information);

    /// <summary>
    /// 按指定最低级别建日志。T7-4：级别来自设置页「高级」组（Config.advanced.logLevel），
    /// 启动时读一次；改级别要重启才生效。
    /// </summary>
    public static LoggerConfiguration Create(IAppPaths paths, LogEventLevel minimumLevel)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.WithProperty("app", "EBookStudio")
            .WriteTo.Async(sink => sink.File(
                FilePathTemplate(paths),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCount,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                // shared: true 而不是 false：false 会独占文件，用户点「打开日志目录」后
                // 双击日志会被系统拒绝访问（实测踩过），本进程也读不了它。
                // 代价是每次写入要开关文件，对个人工具的日志量无所谓。
                shared: true,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                outputTemplate: OutputTemplate))
            .WriteTo.Sink(LogRingBuffer.Instance);
    }
}