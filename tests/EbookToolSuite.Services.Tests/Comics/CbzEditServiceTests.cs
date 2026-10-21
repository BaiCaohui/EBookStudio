using System.IO.Compression;
using System.Text;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Comics;
using EbookToolSuite.Services.Comics;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Comics;

/// <summary>
/// 编辑书库里已有 CBZ 的往返：解压到工作区 → 改 → 重新打包覆盖原文件（先备份）。
/// 用户 2026-10-21：详情窗点「编辑」走这条链路，保存 = 解压、修改、再次打包。
/// </summary>
/// <remarks>
/// 全部真实文件系统 + 真实打包器（笔者的测试约定）：这条链路的价值就在「真的解压、
/// 真的替换了磁盘上的文件」，mock 掉任何一段都测不出东西。
/// </remarks>
public sealed class CbzEditServiceTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task 打开_解压出全部图片并按文件名自然序_读回ComicInfo()
    {
        await using var sandbox = new ImportSandbox();
        var service = CreateService(sandbox);
        var cbz = await WriteCbzAsync(sandbox, "3.png", "1.png", "10.png", "2.png");

        var workspace = await service.OpenAsync(cbz);

        try
        {
            Assert.Equal(4, workspace.Images.Count);

            // 顺序必须 = 包内文件名自然序（阅读器看到的就是这个），不是写入顺序
            Assert.Equal(
                new[] { "1.png", "2.png", "3.png", "10.png" },
                workspace.Images.Select(Path.GetFileName).ToArray());

            // ComicInfo.xml 要读回来预填表单
            Assert.Equal("测试漫画", workspace.Metadata.Title);
            Assert.Equal("测试作者", workspace.Metadata.Writer);

            // 解压出来的都是真实文件
            Assert.All(workspace.Images, path => Assert.True(File.Exists(path)));
        }
        finally
        {
            service.Cleanup(workspace);
        }

        Assert.False(Directory.Exists(workspace.WorkspaceDir));
    }

    [Fact]
    public async Task 保存_按新顺序重新打包覆盖原文件_并留下原件备份()
    {
        await using var sandbox = new ImportSandbox();
        var service = CreateService(sandbox);
        var cbz = await WriteCbzAsync(sandbox, "1.png", "2.png", "3.png");

        var originalBytes = File.ReadAllBytes(cbz);
        var workspace = await service.OpenAsync(cbz);

        try
        {
            // 模拟编辑窗的产出：倒序 + 整表重编号 + 改书名
            var entries = workspace.Images
                .AsEnumerable()
                .Reverse()
                .Select(path => new CbzPackEntry(path, Path.GetFileName(path)))
                .ToList();
            var ordered = CbzEntryNames.ApplyOrder(entries.Select(e => e.EntryName).ToList());
            var finalEntries = entries
                .Select((entry, index) => entry with { EntryName = ordered[index] })
                .ToList();

            var metadata = workspace.Metadata with { Title = "改过的书名" };

            var saved = await service.SaveAsync(workspace, finalEntries, metadata);

            Assert.Equal(cbz, saved.OutputPath);
            Assert.Equal(3, saved.ImageCount);
            Assert.Equal(new FileInfo(cbz).Length, saved.SizeBytes);

            // 原文件真的被替换了
            Assert.False(originalBytes.SequenceEqual(File.ReadAllBytes(cbz)));

            // 备份存在且是替换前的原件
            var backups = Directory.GetFiles(sandbox.Paths.BackupFilesDir, "*.bak");
            Assert.Single(backups);
            Assert.True(originalBytes.SequenceEqual(File.ReadAllBytes(backups[0])));

            // 重打包后的包内顺序 = 用户排的顺序，ComicInfo.xml 是第一个条目
            using var zip = ZipFile.OpenRead(cbz);
            var names = zip.Entries.Select(e => e.FullName).ToList();

            Assert.Equal(ComicInfoXml.EntryName, names[0]);
            Assert.Equal(
                finalEntries.Select(e => e.EntryName).ToArray(),
                names.Skip(1).ToArray());

            var infoEntry = zip.GetEntry(ComicInfoXml.EntryName)!;
            using var reader = new StreamReader(infoEntry.Open(), Encoding.UTF8);
            var xml = reader.ReadToEnd();
            Assert.Contains("<Title>改过的书名</Title>", xml, StringComparison.Ordinal);
        }
        finally
        {
            service.Cleanup(workspace);
        }
    }

    [Fact]
    public async Task 打开_不是漫画包时抛AppException_且不留下工作区()
    {
        await using var sandbox = new ImportSandbox();
        var service = CreateService(sandbox);

        var fake = Path.Combine(sandbox.SourceDir, "not-a-comic.cbz");
        await File.WriteAllTextAsync(fake, "这不是压缩包");

        var workspacesBefore = Directory.GetDirectories(sandbox.Paths.WorkspacesDir).Length;

        await Assert.ThrowsAsync<AppException>(() => service.OpenAsync(fake));

        Assert.Equal(workspacesBefore, Directory.GetDirectories(sandbox.Paths.WorkspacesDir).Length);
    }

    private static CbzEditService CreateService(ImportSandbox sandbox)
        => new(new CbzPacker(), sandbox.Paths, NullLogger<CbzEditService>.Instance);

    /// <summary>造一本带 ComicInfo.xml 的 CBZ，返回路径。</summary>
    private static async Task<string> WriteCbzAsync(ImportSandbox sandbox, params string[] imageNames)
    {
        var folder = Path.Combine(sandbox.SourceDir, "cbz-edit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);

        foreach (var name in imageNames)
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, name), OnePixelPng);
        }

        var packer = new CbzPacker();
        var entries = packer.ScanImages(folder)
            .Select(path => new CbzPackEntry(path, Path.GetFileName(path)))
            .ToList();

        var metadata = new ComicMetadata
        {
            Title = "测试漫画",
            Writer = "测试作者",
            RightToLeft = true,
        };

        var output = Path.Combine(sandbox.SourceDir, "source-" + Guid.NewGuid().ToString("N")[..8] + ".cbz");
        await packer.PackAsync(entries, metadata, output);

        return output;
    }
}
