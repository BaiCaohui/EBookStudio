using System.Text;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Services.Editing;

namespace EbookToolSuite.Services.Tests.Editing;

/// <summary>
/// 还原点 / 备份与还原（T3-6）。真实文件系统 + 真实清单文件，不 mock。
/// 覆盖执行计划的判据：保存后 Backups 里有还原点（1）、改坏后能还原（2）、
/// 还原前自动建反向还原点（3）、超上限清理最旧的（4）、替换前/后强杀相关的落盘状态（5/6）、
/// 以及 operation_log 里要有记录（7，这里用替身收日志）。
/// </summary>
public sealed class RestorePointServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly FakeOperationLog _operations = new();
    private readonly RestorePointService _service;

    public RestorePointServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ets-restore-" + Guid.NewGuid().ToString("N"));
        var appRoot = Path.Combine(_root, "app");
        var userLocal = Path.Combine(_root, "localappdata", "EbookToolSuite");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(userLocal);

        _paths = AppPaths.Create(new AppPathsOptions
        {
            AppRoot = appRoot,
            UserLocalRoot = userLocal,
            Fallback = DataDirFallback.Throw,
        });
        _paths.EnsureCreated();

        _service = new RestorePointService(_paths, _operations);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响结论
        }
    }

    // ---------- 判据 1 ----------

    [Fact]
    public async Task 创建还原点_目录与清单都在_字段名按设计文档()
    {
        var book = Guid.NewGuid();
        var format = Guid.NewGuid();
        var file = MakeFile("书.epub", 4096);

        var point = await _service.CreateAsync(RestorePointTypes.Save, new[]
        {
            new RestorePointRequest(book, format, file),
        });

        Assert.Equal(RestorePointTypes.Save, point.Type);
        Assert.Equal(1, point.ItemCount);
        Assert.Equal(4096, point.TotalBytes);

        var directory = _service.PointDirectory(point.Id);
        Assert.True(Directory.Exists(directory));
        Assert.StartsWith(Path.GetFullPath(_paths.RestorePointsDir), Path.GetFullPath(directory));

        // 备份文件与清单
        var manifest = Path.Combine(directory, RestorePointService.ManifestFileName);
        Assert.True(File.Exists(manifest));

        var json = await File.ReadAllTextAsync(manifest);
        foreach (var key in new[]
                 {
                     "\"restorePointId\"", "\"type\"", "\"createdAt\"", "\"items\"",
                     "\"bookId\"", "\"formatId\"", "\"originalPath\"", "\"backupFile\"",
                     "\"originalSize\"", "\"originalHash\"",
                 })
        {
            Assert.Contains(key, json, StringComparison.Ordinal);
        }

        Assert.Contains("sha256:", json, StringComparison.Ordinal);
        Assert.Equal(
            await RestorePointService.HashAsync(file),
            point.Items[0].OriginalHash);
    }

    // ---------- 判据 2 + 3 ----------

    [Fact]
    public async Task 还原_内容恢复且自动建了反向还原点()
    {
        var book = Guid.NewGuid();
        var format = Guid.NewGuid();
        var file = MakeFile("书.epub", 2048, seed: 7);
        var original = await File.ReadAllBytesAsync(file);

        var point = await _service.CreateAsync(RestorePointTypes.Save, new[]
        {
            new RestorePointRequest(book, format, file),
        });

        // 把书改坏（模拟"编辑改错了"）
        await File.WriteAllTextAsync(file, "改坏了", new UTF8Encoding(false));
        Assert.NotEqual(original, await File.ReadAllBytesAsync(file));

        var result = await _service.RestoreAsync(point.Id);

        Assert.True(result.Success);
        Assert.Equal(1, result.RestoredCount);
        Assert.Equal(original, await File.ReadAllBytesAsync(file));

        // 判据 3：还原前自动建了"反向还原点"
        Assert.False(string.IsNullOrWhiteSpace(result.SafetyRestorePointId));
        var safety = await _service.GetAsync(result.SafetyRestorePointId);
        Assert.NotNull(safety);
        Assert.Equal(RestorePointTypes.Restore, safety!.Type);

        // 反向还原点里存的是"改坏之后"的内容，所以它能再退回去
        await _service.RestoreAsync(safety.Id);
        Assert.NotEqual(original, await File.ReadAllBytesAsync(file));
        Assert.Equal("改坏了", await File.ReadAllTextAsync(file, Encoding.UTF8));
    }

    [Fact]
    public async Task 还原点可以反复用_备份文件不会被消耗()
    {
        var file = MakeFile("书.epub", 512, seed: 3);
        var original = await File.ReadAllBytesAsync(file);
        var point = await _service.CreateAsync(RestorePointTypes.Save, new[]
        {
            new RestorePointRequest(Guid.NewGuid(), Guid.NewGuid(), file),
        });

        await File.WriteAllTextAsync(file, "坏 1", new UTF8Encoding(false));
        await _service.RestoreAsync(point.Id);
        await File.WriteAllTextAsync(file, "坏 2", new UTF8Encoding(false));
        var second = await _service.RestoreAsync(point.Id);

        Assert.True(second.Success);
        Assert.Equal(original, await File.ReadAllBytesAsync(file));
        // 备份本体还在（如果按计划原文用 File.Replace(backup, original)，这里就已经没了）
        Assert.True(File.Exists(Path.Combine(_service.PointDirectory(point.Id), point.Items[0].BackupFile)));
    }

    // ---------- 判据 4 ----------

    [Fact]
    public async Task 单本书只留五份_超出的最旧被清理()
    {
        var book = Guid.NewGuid();
        var format = Guid.NewGuid();
        var file = MakeFile("书.epub", 256, seed: 11);

        for (var i = 0; i < 6; i++)
        {
            await _service.CreateAsync(RestorePointTypes.Save, new[]
            {
                new RestorePointRequest(book, format, file),
            });
        }

        var before = await _service.ListAsync();
        Assert.Equal(6, before.Count);
        var expectedRemoved = before[^1].Id;   // 列表新的在前，最后一个是"最旧的"

        var report = await _service.EnforceLimitsAsync(maxTotalBytes: long.MaxValue, maxPerBook: 5);

        Assert.Equal(1, report.RemovedByPerBookLimit);
        Assert.Equal(expectedRemoved, Assert.Single(report.RemovedRestorePoints));
        Assert.False(Directory.Exists(_service.PointDirectory(expectedRemoved)));
        Assert.Equal(5, (await _service.ListAsync()).Count);
    }

    [Fact]
    public async Task 超过总量上限_从最旧的开始清到不超上限并给出报告()
    {
        var file = MakeFile("书.epub", 400 * 1024, seed: 5);
        for (var i = 0; i < 3; i++)
        {
            await _service.CreateAsync(RestorePointTypes.Save, new[]
            {
                new RestorePointRequest(Guid.NewGuid(), Guid.NewGuid(), file),
            });
        }

        var total = await _service.TotalBytesAsync();
        Assert.True(total > 400 * 1024);

        // 上限设成"只够放一份"
        var report = await _service.EnforceLimitsAsync(maxTotalBytes: 450 * 1024, maxPerBook: 99);

        Assert.True(report.AnythingRemoved, "应该清理了");
        Assert.True(report.FreedBytes > 0);
        Assert.True(await _service.TotalBytesAsync() <= 450 * 1024);

        // 报告里给出的删除清单要跟磁盘一致
        foreach (var id in report.RemovedRestorePoints)
        {
            Assert.False(Directory.Exists(_service.PointDirectory(id)));
        }
    }

    // ---------- 判据 5 / 6 的落盘状态 ----------

    [Fact]
    public async Task 备份完成但还没替换时_原文件依旧原样且备份已在()
    {
        var file = MakeFile("书.epub", 1024, seed: 13);
        var original = await File.ReadAllBytesAsync(file);

        var point = await _service.CreateAsync(RestorePointTypes.Save, new[]
        {
            new RestorePointRequest(Guid.NewGuid(), Guid.NewGuid(), file),
        });

        // 这个状态 = 保存流程走完第 6 步（备份好了）、还没做第 7 步（File.Replace）。
        // 此刻强杀程序：原文件必须完好，备份必须在重启用'还原'也能回到这一刻。
        Assert.Equal(original, await File.ReadAllBytesAsync(file));
        var backup = Path.Combine(_service.PointDirectory(point.Id), point.Items[0].BackupFile);
        Assert.True(File.Exists(backup));
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
    }

    [Fact]
    public async Task 替换之后_新内容在_备份也还在()
    {
        var file = MakeFile("书.epub", 1024, seed: 17);
        var point = await _service.CreateAsync(RestorePointTypes.Save, new[]
        {
            new RestorePointRequest(Guid.NewGuid(), Guid.NewGuid(), file),
        });

        // 模拟第 7 步：原子替换
        var staged = file + ".ets-stage.epub";
        await File.WriteAllTextAsync(staged, "新版本内容", new UTF8Encoding(false));
        File.Replace(staged, file, destinationBackupFileName: null, ignoreMetadataErrors: true);

        // 此刻强杀程序：新文件在，备份也在（所以还能回退）
        Assert.Equal("新版本内容", await File.ReadAllTextAsync(file, Encoding.UTF8));
        Assert.True(File.Exists(Path.Combine(_service.PointDirectory(point.Id), point.Items[0].BackupFile)));
    }

    // ---------- 边界与日志 ----------

    [Fact]
    public async Task 源文件不存在_中止且不留半成品目录()
    {
        var before = Directory.GetDirectories(_paths.RestorePointsDir).Length;

        var ex = await Assert.ThrowsAsync<AppException>(() => _service.CreateAsync(
            RestorePointTypes.Save,
            new[] { new RestorePointRequest(Guid.NewGuid(), Guid.NewGuid(), Path.Combine(_root, "没有这个文件.epub")) }));

        Assert.Equal(ErrorCode.FileNotFound, ex.Code);
        Assert.Equal(before, Directory.GetDirectories(_paths.RestorePointsDir).Length);
    }

    [Fact]
    public async Task 非法编号_抛InvalidPath()
    {
        Assert.Throws<AppException>(() => _service.PointDirectory("../../跑出去"));
        await Assert.ThrowsAsync<AppException>(() => _service.GetAsync("..\\..\\x"));
    }

    [Fact]
    public async Task 清单读不回来_列表里跳过而不是炸()
    {
        var file = MakeFile("书.epub", 128, seed: 19);
        var point = await _service.CreateAsync(RestorePointTypes.Save, new[]
        {
            new RestorePointRequest(Guid.NewGuid(), Guid.NewGuid(), file),
        });

        await File.WriteAllTextAsync(
            Path.Combine(_service.PointDirectory(point.Id), RestorePointService.ManifestFileName),
            "{ 这不是 JSON",
            new UTF8Encoding(false));

        Assert.Empty(await _service.ListAsync());
    }

    [Fact]
    public async Task 还原与清理都写operation_log()
    {
        var book = Guid.NewGuid();
        var format = Guid.NewGuid();
        var file = MakeFile("书.epub", 256, seed: 23);
        var point = await _service.CreateAsync(RestorePointTypes.Save, new[]
        {
            new RestorePointRequest(book, format, file),
        });

        await _service.RestoreAsync(point.Id);
        Assert.Contains(_operations.Entries, e => e.Operation == OperationNames.Restore);

        // 同一本书再存一次：单本上限设成 1，旧的那个就该被清理
        await _service.CreateAsync(RestorePointTypes.Save, new[]
        {
            new RestorePointRequest(book, format, file),
        });

        var report = await _service.EnforceLimitsAsync(maxTotalBytes: long.MaxValue, maxPerBook: 1);
        Assert.True(report.AnythingRemoved);
        Assert.Contains(_operations.Entries, e => e.Operation == OperationNames.Prune);
    }

    // ---------- 辅助 ----------

    private string MakeFile(string name, int bytes, int seed = 1)
    {
        var directory = Path.Combine(_root, "books");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var content = new byte[bytes];
        new Random(seed).NextBytes(content);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>收 operation_log 的替身（T3-6 只要求"记下来"，真库那套由仓储测试覆盖）。</summary>
    private sealed class FakeOperationLog : IOperationLogRepository
    {
        public List<OperationLogEntry> Entries { get; } = new();

        public Task<long> AddAsync(OperationLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.FromResult((long)Entries.Count);
        }

        public Task<IReadOnlyList<OperationLogEntry>> ListRecentAsync(int take = 100, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<OperationLogEntry>)Entries.Take(take).ToList());

        public Task<IReadOnlyList<OperationLogEntry>> ListForBookAsync(
            Guid bookId, int take = 50, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<OperationLogEntry>)Entries
                .Where(e => e.BookId == bookId)
                .Take(take)
                .ToList());

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(Entries.Count);
    }
}
