using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 用户自己「另存为」的转换模板。
/// </summary>
/// <remarks>
/// 内置模板**不在**这张表里：它们以代码为唯一来源（ConversionTemplates.cs），
/// 用户不能删也不能改，只能「另存为」出一份自己的。所以这里读出来的每一条
/// 都是用户模板。
/// </remarks>
public interface IConversionTemplateRepository
{
    /// <summary>全部用户模板，按 id 升序（稳定的展示顺序）。</summary>
    Task<IReadOnlyList<ConversionTemplate>> ListAsync(CancellationToken ct = default);

    /// <summary>取一条；不存在返回 null。</summary>
    Task<ConversionTemplate?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>新增或整行覆盖（同 id 视为覆盖，用于「另存为」时改名保存）。</summary>
    Task UpsertAsync(ConversionTemplate template, CancellationToken ct = default);

    /// <summary>删除一条。返回是否真的删掉了。</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
