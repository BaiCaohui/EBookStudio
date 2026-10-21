namespace EbookToolSuite.Core.Interfaces;

/// <summary>数据库生命周期入口。App 启动时调用一次 InitializeAsync。</summary>
public interface IDatabase
{
    /// <summary>
    /// 建库/迁移到当前程序支持的版本。幂等：已是最新版本时什么都不做。
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>当前库的 user_version，诊断与测试用。</summary>
    Task<int> GetSchemaVersionAsync(CancellationToken ct = default);
}