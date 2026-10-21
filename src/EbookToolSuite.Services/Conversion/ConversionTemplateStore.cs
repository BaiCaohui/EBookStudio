using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Conversion;

/// <summary>
/// 模板总目录：内置模板（来自代码）+ 用户「另存为」的模板（来自数据库）。
/// </summary>
/// <remarks>
/// 内置优先：同 id 时以代码里的为准（用户改不了内置模板，只能另存为一份新的）。
/// 用户模板只在第一次用到时从库里读一次，之后走内存；Save/Delete 之后立刻重读。
/// 库里读不出来（表被改坏、库锁住）时**降级为只有内置模板**并记一条 WRN，
/// 不让「模板列表读不出来」变成「完全不能转换」。
/// </remarks>
public sealed class ConversionTemplateStore
{
    private readonly IConversionTemplateRepository? _repo;
    private readonly ILogger<ConversionTemplateStore>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<ConversionTemplate> _user = Array.Empty<ConversionTemplate>();
    private bool _loaded;

    public ConversionTemplateStore(
        IConversionTemplateRepository? repo = null,
        ILogger<ConversionTemplateStore>? log = null)
    {
        _repo = repo;
        _log = log;
    }

    /// <summary>用户模板（内存快照）。调之前先 EnsureLoadedAsync。</summary>
    public IReadOnlyList<ConversionTemplate> UserTemplates => _user;

    /// <summary>把用户模板读进内存。幂等，并发安全。</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded || _repo is null)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            _user = await _repo.ListAsync(ct).ConfigureAwait(false);
            _loaded = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "读用户转换模板失败，先只用内置模板");
            _user = Array.Empty<ConversionTemplate>();
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按 id 找模板：内置优先，再查用户模板。找不到返回 null。</summary>
    public ConversionTemplate? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return ConversionTemplates.Find(id)
            ?? _user.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>能产出某个目标格式的模板（只含换格式的，不含 polish）。</summary>
    public IReadOnlyList<ConversionTemplate> ForTarget(BookFormatKind target)
        => ConversionTemplates.ForTarget(target)
            .Concat(_user.Where(t => !t.KeepsFormat
                && t.Engine == ConversionEngine.Calibre
                && t.TargetFormat == target))
            .ToList();

    /// <summary>「不换格式，只优化」的模板，且必须适用于这个源格式。</summary>
    public IReadOnlyList<ConversionTemplate> FormatKeeping(BookFormatKind sourceFormat)
        => ConversionTemplates.FormatKeepingFor(sourceFormat)
            .Concat(_user.Where(t => t.KeepsFormat && t.AppliesTo(sourceFormat)))
            .ToList();

    /// <summary>保存一份用户模板（同 id 覆盖）。</summary>
    public async Task SaveUserAsync(ConversionTemplate template, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (_repo is null)
        {
            throw new InvalidOperationException("没有配置用户模板仓储，无法保存模板。");
        }

        await _repo.UpsertAsync(template, ct).ConfigureAwait(false);
        await ReloadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>删掉一份用户模板。内置模板删不掉（它们不在库里）。</summary>
    public async Task<bool> DeleteUserAsync(string id, CancellationToken ct = default)
    {
        if (_repo is null)
        {
            return false;
        }

        var removed = await _repo.DeleteAsync(id, ct).ConfigureAwait(false);
        await ReloadAsync(ct).ConfigureAwait(false);
        return removed;
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _user = _repo is null
                ? Array.Empty<ConversionTemplate>()
                : await _repo.ListAsync(ct).ConfigureAwait(false);
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
