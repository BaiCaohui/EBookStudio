using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Infrastructure.Data;

/// <summary>
/// 迁移执行器（设计文档 5.3）。版本号存在 PRAGMA user_version，脚本在 Migrations/*.sql。
/// 纪律：只追加不修改已发布脚本；脚本内 DDL 一律 IF NOT EXISTS。
/// </summary>
public sealed class SchemaMigrator
{
    /// <summary>当前程序支持的 schema 版本。每加一个脚本 +1。</summary>
    public const int TargetVersion = 11;

    private const string ResourcePrefix = "EbookToolSuite.Infrastructure.Data.Migrations.";

    private readonly ILogger<SchemaMigrator> _log;

    public SchemaMigrator(ILogger<SchemaMigrator> log) => _log = log;

    public async Task MigrateAsync(SqliteConnection conn, CancellationToken ct = default)
    {
        var current = await conn.ScalarAsync<int>("PRAGMA user_version;", ct: ct).ConfigureAwait(false);

        if (current > TargetVersion)
        {
            throw new AppException(
                ErrorCode.InternalError,
                $"数据库版本({current})高于本程序支持的版本({TargetVersion})，"
                + "请使用较新版本的程序，或从备份还原数据库。",
                technical: $"user_version={current} TargetVersion={TargetVersion}");
        }

        if (current == TargetVersion)
        {
            return;
        }

        for (var v = current + 1; v <= TargetVersion; v++)
        {
            var sql = LoadMigrationSql(v);

            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await conn.ExecuteAsync(sql, tx: tx, ct: ct).ConfigureAwait(false);
            await conn.ExecuteAsync($"PRAGMA user_version = {v};", tx: tx, ct: ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            _log.LogInformation("数据库迁移到 v{Version}", v);
        }
    }

    /// <summary>取内嵌的迁移脚本。找不到时把可用资源名列进异常，便于排错。</summary>
    private static string LoadMigrationSql(int version)
    {
        var asm = typeof(SchemaMigrator).Assembly;
        var prefix = $"{ResourcePrefix}{version:D4}_";
        var names = asm.GetManifestResourceNames();
        var name = Array.Find(names, n => n.StartsWith(prefix, StringComparison.Ordinal))
            ?? throw new AppException(
                ErrorCode.InternalError,
                "程序内部的数据库迁移脚本缺失，请重新安装或拷贝完整的程序目录。",
                technical: $"找不到资源前缀 {prefix}；现有资源：{string.Join(", ", names)}");

        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new AppException(
                ErrorCode.InternalError,
                "程序内部的数据库迁移脚本读不出来，请重新安装或拷贝完整的程序目录。",
                technical: $"GetManifestResourceStream 返回 null: {name}");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
