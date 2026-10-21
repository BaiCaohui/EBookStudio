using System.Globalization;
using Microsoft.Data.Sqlite;

namespace EbookToolSuite.Infrastructure.Data;

/// <summary>
/// 手写 ADO 的最小辅助层：执行、读标量、映射成列表，外加几个 NULL 安全的取值器。
/// 所有 SQL 一律走 SqliteParameter，禁止字符串拼接（设计文档 5.4）。
/// </summary>
public static class SqliteExtensions
{
    public static async Task<int> ExecuteAsync(
        this SqliteConnection conn,
        string sql,
        IReadOnlyList<SqliteParameter>? parameters = null,
        SqliteTransaction? tx = null,
        CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        AddParameters(cmd, parameters);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T> ScalarAsync<T>(
        this SqliteConnection conn,
        string sql,
        IReadOnlyList<SqliteParameter>? parameters = null,
        SqliteTransaction? tx = null,
        CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        AddParameters(cmd, parameters);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (value is null || value is DBNull)
        {
            return default!;
        }

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture)!;
    }

    public static async Task<List<T>> QueryAsync<T>(
        this SqliteConnection conn,
        string sql,
        Func<SqliteDataReader, T> map,
        IReadOnlyList<SqliteParameter>? parameters = null,
        SqliteTransaction? tx = null,
        CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        AddParameters(cmd, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<T>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(map(reader));
        }

        return list;
    }

    /// <summary>新建一个参数；null 会写成 SQL NULL。</summary>
    public static SqliteParameter P(string name, object? value)
        => new(name, value ?? DBNull.Value);

    private static void AddParameters(SqliteCommand cmd, IReadOnlyList<SqliteParameter>? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var p in parameters)
        {
            cmd.Parameters.Add(p);
        }
    }

    // ---------- NULL 安全的 reader 取值器 ----------

    public static string? Text(this SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : r.GetString(i);

    public static int? Int32OrNull(this SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : r.GetInt32(i);

    public static long? Int64OrNull(this SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : r.GetInt64(i);

    public static double? DoubleOrNull(this SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : r.GetDouble(i);

    public static Guid GuidValue(this SqliteDataReader r, int i)
        => Guid.Parse(r.GetString(i));

    public static Guid? GuidOrNull(this SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i));
}