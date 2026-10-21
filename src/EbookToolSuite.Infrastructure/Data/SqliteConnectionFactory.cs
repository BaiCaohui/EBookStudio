using EbookToolSuite.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace EbookToolSuite.Infrastructure.Data;

/// <summary>
/// 连接工厂。设计文档 5.4：写操作走短事务，UI 查询走一条共享长连接
/// （WAL 允许一写多读）。连接级 pragma 在每次打开时统一应用。
/// </summary>
public sealed class SqliteConnectionFactory : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _sharedGate = new(1, 1);
    private SqliteConnection? _shared;
    private bool _disposed;

    public SqliteConnectionFactory(IAppPaths paths)
    {
        DatabaseFile = Path.GetFullPath(paths.DbFile);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,

            // 由驱动保证每条连接都开外键（等价于 PRAGMA foreign_keys=ON）。
            // 它是连接级开关，不能只在外键建表时设一次。
            ForeignKeys = true,
        }.ToString();
    }

    public string DatabaseFile { get; }

    /// <summary>新开一条连接（已打开并应用 pragma）。调用方负责 Dispose。</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabaseFile)!);

            var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await ApplyPragmasAsync(conn, ct).ConfigureAwait(false);
            return conn;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // T7-2 第 13 项：库里被占用 / 盘被拔掉 / 文件损坏都要变成能看懂的中文，
            // 而不是让 SqliteException 的英文原文一路冒到界面上。
            throw SqliteErrors.Translate(ex, "打开书库");
        }
    }

    /// <summary>
    /// 共享读连接：串行化访问（SqliteConnection 非线程安全），
    /// 回调里拿到的是同一个实例，**不要 Dispose 它**。
    /// </summary>
    public async Task<T> WithSharedReadAsync<T>(
        Func<SqliteConnection, Task<T>> work,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _sharedGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _shared ??= await OpenAsync(ct).ConfigureAwait(false);
            return await work(_shared).ConfigureAwait(false);
        }
        finally
        {
            _sharedGate.Release();
        }
    }

    private static async Task ApplyPragmasAsync(SqliteConnection conn, CancellationToken ct)
    {
        // journal_mode 会返回一行结果，必须读掉，所以这里用 Scalar 而不是 Execute
        await conn.ScalarAsync<string>("PRAGMA journal_mode=WAL;", ct: ct).ConfigureAwait(false);
        await conn.ExecuteAsync("PRAGMA synchronous=NORMAL;", ct: ct).ConfigureAwait(false);
        await conn.ExecuteAsync("PRAGMA busy_timeout=5000;", ct: ct).ConfigureAwait(false);
        await conn.ExecuteAsync("PRAGMA temp_store=MEMORY;", ct: ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_shared is not null)
        {
            await _shared.DisposeAsync().ConfigureAwait(false);
            _shared = null;
        }

        _sharedGate.Dispose();
    }
}