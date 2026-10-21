using System.Text.Json;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Data.Sqlite;
using static EbookToolSuite.Infrastructure.Data.SqliteExtensions;

namespace EbookToolSuite.Infrastructure.Data.Repositories;

/// <summary>
/// 用户自定义转换模板的仓储。手写 ADO，与其它仓储一致。
/// </summary>
/// <remarks>
/// 内置模板永远不在这张表里（代码是唯一来源），所以 Upsert/Delete 只作用于用户模板。
/// arguments_json 直接存 System.Text.Json 序列化后的 ConversionArgument 数组。
/// </remarks>
public sealed class ConversionTemplateRepository : IConversionTemplateRepository
{
    private const string Columns =
        "id, name, out_format, arguments_json, description, is_builtin, updated_at";

    private const string ListSql =
        "SELECT " + Columns + " FROM conversion_templates ORDER BY id;";

    private const string GetSql =
        "SELECT " + Columns + " FROM conversion_templates WHERE id = @id;";

    private const string UpsertSql =
        """
        INSERT INTO conversion_templates (
            id, name, out_format, arguments_json, description, is_builtin, updated_at)
        VALUES (
            @id, @name, @out_format, @arguments_json, @description, @is_builtin, @updated_at)
        ON CONFLICT(id) DO UPDATE SET
            name = excluded.name,
            out_format = excluded.out_format,
            arguments_json = excluded.arguments_json,
            description = excluded.description,
            is_builtin = excluded.is_builtin,
            updated_at = excluded.updated_at;
        """;

    private const string DeleteSql = "DELETE FROM conversion_templates WHERE id = @id;";

    private readonly SqliteConnectionFactory _factory;

    public ConversionTemplateRepository(SqliteConnectionFactory factory) => _factory = factory;

    public Task<IReadOnlyList<ConversionTemplate>> ListAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var rows = await conn.QueryAsync(ListSql, Map, null, null, ct).ConfigureAwait(false);
            return (IReadOnlyList<ConversionTemplate>)rows;
        }, ct);

    public Task<ConversionTemplate?> GetAsync(string id, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var rows = await conn
                .QueryAsync(GetSql, Map, new[] { P("@id", id) }, null, ct)
                .ConfigureAwait(false);
            return rows.Count > 0 ? rows[0] : null;
        }, ct);

    public async Task UpsertAsync(ConversionTemplate template, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(UpsertSql, Params(template), null, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var affected = await conn
            .ExecuteAsync(DeleteSql, new[] { P("@id", id) }, null, ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    private static ConversionTemplate Map(SqliteDataReader r)
    {
        // out_format 为空 = 「不换格式」的用户模板。引擎由此推导，不再单开一列：
        // 加列要 ALTER TABLE，而 SQLite 没有 ADD COLUMN IF NOT EXISTS，
        // 会破坏「迁移脚本必须能重跑」这条纪律（DatabaseTests.user_version_归零后重建 看守）。
        var outFormat = r.Text(2);
        var target = string.IsNullOrWhiteSpace(outFormat)
            ? (BookFormatKind?)null
            : BookFormatKinds.FromStorageString(outFormat);
        var engine = target is null ? ConversionEngine.Polish : ConversionEngine.Calibre;

        return new ConversionTemplate
        {
            Id = r.Text(0) ?? string.Empty,
            DisplayName = r.Text(1) ?? string.Empty,
            TargetFormat = target,
            Engine = engine,
            ApplicableFormats = ConversionEngineRules.ApplicableFormats(engine),
            Note = r.Text(4),
            IsBuiltIn = (r.Int32OrNull(5) ?? 0) == 1,
            Arguments = ParseArguments(r.Text(3)),
        };
    }

    private static IReadOnlyList<ConversionArgument> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<ConversionArgument>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<ConversionArgument>>(json)
                ?? (IReadOnlyList<ConversionArgument>)Array.Empty<ConversionArgument>();
        }
        catch (JsonException)
        {
            // 手改数据库改坏了不该让整个模板列表读不出来：当成「没有参数」。
            return Array.Empty<ConversionArgument>();
        }
    }

    private static IReadOnlyList<BookFormatKind> SplitFormats(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<BookFormatKind>();
        }

        var result = new List<BookFormatKind>();
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var kind = BookFormatKinds.FromStorageString(part);
            if (kind != BookFormatKind.Unknown && !result.Contains(kind))
            {
                result.Add(kind);
            }
        }

        return result;
    }

    private static List<SqliteParameter> Params(ConversionTemplate t) => new()
    {
        P("@id", t.Id),
        P("@name", t.DisplayName),
        // out_format 是 NOT NULL，所以「不换格式」存空串（读回来还是 null -> Polish）。
        P("@out_format", t.TargetFormat?.ToStorageString() ?? string.Empty),
        P("@arguments_json", JsonSerializer.Serialize(t.Arguments ?? Array.Empty<ConversionArgument>())),
        P("@description", t.Note),
        P("@is_builtin", t.IsBuiltIn ? 1 : 0),
        P("@updated_at", UnixTime.ToMillis(DateTimeOffset.UtcNow)),
    };
}
