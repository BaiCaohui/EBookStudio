using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using Microsoft.Data.Sqlite;

namespace EbookToolSuite.Infrastructure.Data;

/// <summary>
/// SQLite 异常 -> <see cref="AppException"/> 的翻译（执行计划 T7-2 第 13 项：
/// 数据库文件被占用时等待重试，超时后给明确提示）。
///
/// 连接上已经设了 <c>PRAGMA busy_timeout=5000</c>：前 5 秒驱动自己等。
/// 等完还抛出来的 <c>SQLITE_BUSY / SQLITE_LOCKED</c> 才是真的被另一个进程占着
/// （典型场景：同时开了两个 EBookStudio），必须让用户看懂该关谁。
/// </summary>
public static class SqliteErrors
{
    /// <summary>翻译任意异常；已经是 AppException 的原样返回。</summary>
    public static AppException Translate(Exception ex, string what)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex is AppException already)
        {
            return already;
        }

        var info = Classify(ex);

        return new AppException(
            info.Code,
            $"{what}：{info.Message}{info.Action}",
            technical: ex.GetType().Name + ": " + ex.Message,
            inner: ex,
            context: new Dictionary<string, object?>
            {
                ["what"] = what,
                ["sqliteErrorCode"] = (ex as SqliteException)?.SqliteErrorCode,
            });
    }

    /// <summary>
    /// 是否是"环境/存储"层面的失败（占用、空间、损坏、只读、打不开）。
    /// 约束冲突（SQLITE_CONSTRAINT=19）这类是程序缺陷，不能翻译成用户话术，要原样暴露。
    /// </summary>
    public static bool IsInfrastructureFailure(SqliteException ex)
        => ex.SqliteErrorCode is 5 or 6 or 10 or 11 or 13 or 14 or 26 or 8;

    /// <summary>分类。文案在 <see cref="ErrorCatalog"/> 的对应条目上做数据库语境细化。</summary>
    public static ErrorInfo Classify(Exception ex)
    {
        if (ex is not SqliteException sqlite)
        {
            return ErrorCatalog.Get(ErrorCode.InternalError);
        }

        return sqlite.SqliteErrorCode switch
        {
            5 or 6 => ErrorCatalog.Get(ErrorCode.FileLocked) with
            {
                Message = "书库正被其他程序占用（例如同时开了两个 EBookStudio），已等待 5 秒仍未释放。",
                Action = "关掉占用它的程序后重试。",
            },

            13 => ErrorCatalog.Get(ErrorCode.DiskFull),

            11 or 26 => ErrorCatalog.Get(ErrorCode.CorruptFile) with
            {
                Message = "书库数据异常（数据库文件损坏）。",
                Action = "从备份还原；或把 Library.db 改名后重启，让程序重建空库。",
            },

            8 => ErrorCatalog.Get(ErrorCode.AccessDenied) with
            {
                Message = "书库文件是只读的（可能在只读介质上，或被设成了只读）。",
                Action = "换一个可写的数据目录后重试。",
            },

            14 or 10 => ErrorCatalog.Get(ErrorCode.DataDirNotWritable) with
            {
                Message = "打不开书库文件（磁盘已断开、目录不可写或文件被锁死）。",
                Action = "检查磁盘是否已连接、目录是否有写入权限，然后重试。",
            },

            _ => ErrorCatalog.Get(ErrorCode.InternalError),
        };
    }
}
