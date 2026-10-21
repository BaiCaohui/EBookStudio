using EbookToolSuite.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Infrastructure.Data;

/// <summary>
/// 数据库生命周期入口。连接级 pragma 由 SqliteConnectionFactory 在每次开连接时应用，
/// 这里只负责把 schema 迁移到当前版本。
/// </summary>
public sealed class Database : IDatabase
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SchemaMigrator _migrator;
    private readonly ILogger<Database> _log;

    public Database(
        SqliteConnectionFactory factory,
        SchemaMigrator migrator,
        ILogger<Database> log)
    {
        _factory = factory;
        _migrator = migrator;
        _log = log;
    }

    public string DatabaseFile => _factory.DatabaseFile;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
            await _migrator.MigrateAsync(conn, ct).ConfigureAwait(false);

            var version = await conn.ScalarAsync<int>("PRAGMA user_version;", ct: ct).ConfigureAwait(false);
            var mode = await conn.ScalarAsync<string>("PRAGMA journal_mode;", ct: ct).ConfigureAwait(false);

            _log.LogInformation(
                "数据库就绪：{File} user_version={Version} journal_mode={Mode}",
                _factory.DatabaseFile,
                version,
                mode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw SqliteErrors.Translate(ex, "初始化书库");
        }
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ScalarAsync<int>("PRAGMA user_version;", ct: ct).ConfigureAwait(false);
    }
}