using System.Diagnostics;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Services.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Library;

/// <summary>
/// 模糊去重（T2-2）。用纯内存假仓储 + 可脚本化的假哈希：
/// 这里验的是「分组 + 懒算 + 复核」的判定逻辑；SQL 与真实耗时另由冒烟脚本对真库验。
/// </summary>
public sealed class DuplicateDetectionServiceTests
{
    private static DuplicateDetectionService NewService(IDuplicateRepository repo, IHashUtil hash)
        => new(repo, hash, NullLogger<DuplicateDetectionService>.Instance);

    /// <summary>判据 1 的逻辑面：同名同作者同格式两本进候选组，内容一致就是确定重复。</summary>
    [Fact]
    public async Task 同名同作者同格式的两本_内容一致时判为确定重复()
    {
        var repo = new FakeDuplicateRepo();
        repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000);
        repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000);
        var hash = new FakeHashUtil(_ => "SAME");

        var report = await NewService(repo, hash).ScanAsync(null, null);

        Assert.Equal(1, report.GroupCount);
        Assert.Equal(2, report.Groups[0].Count);
        Assert.True(report.Groups[0].AllHashesEqual);
        Assert.Equal(2, hash.Calls);
        Assert.Equal(2, repo.SavedHashes.Count);
        Assert.Equal(1, report.ConfirmedGroupCount);
    }

    /// <summary>判据 2：同一本书的 EPUB 与 MOBI 不算重复（格式算进键）。</summary>
    [Fact]
    public async Task 同一本书的EPUB与MOBI_不算重复()
    {
        var repo = new FakeDuplicateRepo();
        repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000);
        repo.AddCandidate("Sample Book J", "Sample Author", "MOBI", 1200);

        var report = await NewService(repo, new FakeHashUtil(_ => "H")).ScanAsync(null, null);

        Assert.True(report.IsEmpty);
        Assert.Equal(2, report.BooksScanned);
    }


    /// <summary>判据 3：同一本书的两个 EPUB 版本（书名带噪声后缀）要出现在结果里。</summary>
    [Fact]
    public async Task 书名带噪声后缀的两本_归到同一组()
    {
        var repo = new FakeDuplicateRepo();
        repo.AddCandidate("《Sample Book B》多看精校版", "作者", "EPUB", 400_000, hash: "X");
        repo.AddCandidate("Sample Book B", "作者", "EPUB", 380_000, hash: "X");
        var hash = new FakeHashUtil(_ => "SHOULD-NOT-BE-CALLED");

        var report = await NewService(repo, hash).ScanAsync(null, null);

        Assert.Equal(1, report.GroupCount);
        Assert.True(report.Groups[0].AllHashesEqual);
        Assert.Equal(0, hash.Calls);
    }

    [Fact]
    public async Task 已有full_hash的不重算_缺的算完写回()
    {
        var repo = new FakeDuplicateRepo();
        repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000, hash: "OLD", filePath: "C:\\lib\\a.epub");
        var b = repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000, filePath: "C:\\lib\\b.epub");
        var hash = new FakeHashUtil(p => p.EndsWith("b.epub", StringComparison.Ordinal) ? "NEW" : "OTHER");

        var report = await NewService(repo, hash).ScanAsync(null, null);

        Assert.Equal(1, hash.Calls);
        Assert.Single(repo.SavedHashes);
        Assert.Equal(b.FormatId, repo.SavedHashes[0].FormatId);
        Assert.Equal("NEW", repo.SavedHashes[0].Hash);
        Assert.False(report.Groups[0].AllHashesEqual);
    }

    /// <summary>快速指纹会碰撞：内容不同时必须说「不同版本」，不能谎报「完全相同」。</summary>
    [Fact]
    public async Task 内容哈希不同_列为可能重复但不判确定重复()
    {
        var repo = new FakeDuplicateRepo();
        repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000, filePath: "C:\\lib\\a.epub");
        repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000, filePath: "C:\\lib\\b.epub");
        var hash = new FakeHashUtil(p => p.EndsWith("a.epub", StringComparison.Ordinal) ? "H1" : "H2");

        var report = await NewService(repo, hash).ScanAsync(null, null);

        Assert.Equal(1, report.GroupCount);
        Assert.False(report.Groups[0].AllHashesEqual);
        Assert.Equal(0, report.ConfirmedGroupCount);
        Assert.Contains("版本", report.Groups[0].Suggestion);
    }


    [Fact]
    public async Task 忽略过的组不再出现_组里来了新成员才重新报()
    {
        var repo = new FakeDuplicateRepo();
        var a = repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000, hash: "X");
        var b = repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000, hash: "X");
        var svc = NewService(repo, new FakeHashUtil(_ => "X"));

        await svc.IgnoreGroupAsync(new[] { a.BookId, b.BookId });
        Assert.True((await svc.ScanAsync(null, null)).IsEmpty);

        repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000, hash: "X");
        var again = await svc.ScanAsync(null, null);

        Assert.Equal(1, again.GroupCount);
        Assert.Equal(3, again.Groups[0].Count);
    }

    [Fact]
    public async Task 组太大就跳过_不把病态数据摊给界面()
    {
        var repo = new FakeDuplicateRepo();
        for (var i = 0; i < 5; i++)
        {
            repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000);
        }

        var report = await NewService(repo, new FakeHashUtil(_ => "H"))
            .ScanAsync(new DuplicateScanOptions { MaxGroupSize = 2 }, null);

        Assert.True(report.IsEmpty);
    }

    [Fact]
    public async Task 关掉哈希复核_仍列出候选但明说无法确证()
    {
        var repo = new FakeDuplicateRepo();
        repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000);
        repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1200);
        var hash = new FakeHashUtil(_ => "H");

        var report = await NewService(repo, hash)
            .ScanAsync(new DuplicateScanOptions { ComputeContentHashes = false }, null);

        Assert.Equal(1, report.GroupCount);
        Assert.Equal(0, hash.Calls);
        Assert.Equal(0, report.HashesComputed);
        Assert.False(report.Groups[0].AllHashesEqual);
        Assert.Contains("无法确证", report.Groups[0].Suggestion);
    }


    /// <summary>判据 4 的逻辑面：一千本书的分组阶段不该有可见耗时（真实耗时由冒烟脚本对真库量）。</summary>
    [Fact]
    public async Task 一千本书_分组阶段不该有可见耗时()
    {
        var repo = new FakeDuplicateRepo();
        for (var i = 0; i < 998; i++)
        {
            repo.AddCandidate("书" + i, "作者" + i, "EPUB", 1000 + i, hash: "H" + i);
        }

        repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000);
        repo.AddCandidate("Sample Book J", "Sample Author", "EPUB", 1000);

        var svc = NewService(repo, new FakeHashUtil(_ => "SAME"));
        var sw = Stopwatch.StartNew();
        var report = await svc.ScanAsync(null, null);
        sw.Stop();

        Assert.Equal(1000, report.BooksScanned);
        Assert.Equal(1, report.GroupCount);
        Assert.True(sw.ElapsedMilliseconds < 2000, "逻辑层耗时 " + sw.ElapsedMilliseconds + " ms");
    }
}

