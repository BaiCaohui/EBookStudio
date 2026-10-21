using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Services.Tests.Library;

/// <summary>纯内存的重复检测仓储替身：记录写回与隐藏动作，供断言用。</summary>
internal sealed class FakeDuplicateRepo : IDuplicateRepository
{
    private readonly List<DuplicateCandidateRow> _rows = new();
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _hidden = new();
    private readonly Dictionary<Guid, string> _fingerprints = new();

    public string? LastFingerprintQuery { get; private set; }

    public List<(Guid FormatId, string Hash)> SavedHashes { get; } = new();

    public List<(Guid Kept, IReadOnlyList<Guid> Copies)> HideCalls { get; } = new();

    public DuplicateCandidateRow AddCandidate(
        string title,
        string authors,
        string format,
        long size,
        string? hash = null,
        string? filePath = null,
        Guid? bookId = null,
        string? fingerprint = null)
    {
        var row = new DuplicateCandidateRow(
            Guid.NewGuid(),
            bookId ?? Guid.NewGuid(),
            title,
            authors,
            format,
            filePath ?? ("C:\\lib\\" + Guid.NewGuid().ToString("N") + "." + format.ToLowerInvariant()),
            size,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            hash,
            0,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        _rows.Add(row);
        if (fingerprint is not null)
        {
            _fingerprints[row.FormatId] = fingerprint;
        }

        return row;
    }


    public Task<DuplicateCandidateRow?> FindByFingerprintAsync(string quickFingerprint, CancellationToken ct = default)
    {
        LastFingerprintQuery = quickFingerprint;
        var row = _rows.FirstOrDefault(
            r => _fingerprints.TryGetValue(r.FormatId, out var fp) && fp == quickFingerprint);
        return Task.FromResult(row);
    }

    public Task<IReadOnlyList<DuplicateCandidateRow>> ListCandidatesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DuplicateCandidateRow>>(
            _rows.Where(r => !_hidden.Contains(r.BookId)).ToList());

    public Task<IReadOnlyList<string>> ListIgnoredSignaturesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(_ignored.ToList());

    public Task SaveContentHashAsync(Guid formatId, string contentHash, CancellationToken ct = default)
    {
        SavedHashes.Add((formatId, contentHash));
        return Task.CompletedTask;
    }

    public Task<int> HideCopiesAsync(
        Guid keptBookId,
        IReadOnlyList<Guid> copyBookIds,
        CancellationToken ct = default)
    {
        HideCalls.Add((keptBookId, copyBookIds));
        foreach (var id in copyBookIds)
        {
            _hidden.Add(id);
        }

        return Task.FromResult(copyBookIds.Count);
    }

    public Task<int> RestoreCopiesAsync(IReadOnlyList<Guid> copyBookIds, CancellationToken ct = default)
    {
        foreach (var id in copyBookIds)
        {
            _hidden.Remove(id);
        }

        return Task.FromResult(copyBookIds.Count);
    }

    public Task IgnoreGroupAsync(string memberKey, CancellationToken ct = default)
    {
        _ignored.Add(memberKey);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Guid>> ListHiddenBookIdsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Guid>>(_hidden.ToList());
}


/// <summary>假哈希：快速指纹恒成功；全文件哈希按 filePath 映射，方便造「指纹碰撞但内容不同」。</summary>
internal sealed class FakeHashUtil : IHashUtil
{
    private readonly Func<string, string> _map;
    private int _calls;

    public FakeHashUtil(Func<string, string> map) => _map = map;

    public int Calls => _calls;

    public Task<string> QuickFingerprintAsync(string path, CancellationToken ct = default)
        => Task.FromResult("QF");

    public Task<string> ComputeContentHashAsync(string path, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(_map(path));
    }
}

