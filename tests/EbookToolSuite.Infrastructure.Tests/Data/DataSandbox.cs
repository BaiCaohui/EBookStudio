using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Data;
using EbookToolSuite.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Infrastructure.Tests.Data;

/// <summary>
/// 一个用完即弃的数据目录：真实的 AppPaths（指向临时目录）+ 真实 SQLite 文件。
/// 不用 mock，因为这一层的价值恰恰在于「真的能建库、真的能落盘」。
/// </summary>
internal sealed class DataSandbox : IAsyncDisposable
{
    public DataSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "ets-data-" + Guid.NewGuid().ToString("N"));
        var appRoot = Path.Combine(Root, "app");
        var userLocal = Path.Combine(Root, "localappdata", "EbookToolSuite");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(userLocal);

        Paths = AppPaths.Create(new AppPathsOptions
        {
            AppRoot = appRoot,
            UserLocalRoot = userLocal,
            Fallback = DataDirFallback.Throw,
        });
        Paths.EnsureCreated();

        Factory = new SqliteConnectionFactory(Paths);
        Database = new Database(
            Factory,
            new SchemaMigrator(NullLogger<SchemaMigrator>.Instance),
            NullLogger<Database>.Instance);
        Repository = new BookRepository(Factory);
        StatsRepository = new ReadingStatsRepository(Factory);
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public SqliteConnectionFactory Factory { get; }

    public Database Database { get; }

    public BookRepository Repository { get; }

    /// <summary>reading_sessions 仓储（T7-3B）。</summary>
    public ReadingStatsRepository StatsRepository { get; }

    public string DbFile => Paths.DbFile;

    /// <summary>绕开业务层直接执行 SQL，用来改 user_version、查 sqlite_master 等。</summary>
    public async Task<int> RawExecuteAsync(string sql)
    {
        await using var conn = await Factory.OpenAsync();
        return await conn.ExecuteAsync(sql);
    }

    public async Task<T> RawScalarAsync<T>(string sql)
    {
        await using var conn = await Factory.OpenAsync();
        return await conn.ScalarAsync<T>(sql);
    }

    public async Task<List<string>> QueryStringsAsync(string sql)
    {
        await using var conn = await Factory.OpenAsync();
        return await conn.QueryAsync(sql, r => r.GetString(0));
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();

        // WAL 的两个伴生文件可能还被释放中的句柄占着一瞬，删不掉就算了
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}