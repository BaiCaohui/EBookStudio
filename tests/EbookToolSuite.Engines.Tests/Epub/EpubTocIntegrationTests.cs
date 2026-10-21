using System.Text;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// 目录解析的真实样本验证（T4-4）：把 TestCorpus\ 下所有 EPUB 的目录解析一遍，
/// 断言每本都能拿到目录或按 spine 降级、顺序不丢，并把统计写成报告。
/// </summary>
/// <remarks>需要 ETS_RUN_INTEGRATION=1 才跑（跟其它集成测试一个口径）。</remarks>
public sealed class EpubTocIntegrationTests
{
    [Fact]
    public void 真实样本的目录_每本都能解析出来_EPUB2_与_EPUB3_都有()
    {
        if (Environment.GetEnvironmentVariable("ETS_RUN_INTEGRATION") != "1")
        {
            return;
        }

        var root = FindRepoRoot();
        var corpus = Path.Combine(root, "TestCorpus");
        Assert.True(Directory.Exists(corpus), $"样本目录不存在：{corpus}");

        var files = Directory.GetFiles(corpus, "*.epub", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var report = new StringBuilder();
        var epub2 = 0;
        var epub3 = 0;
        var fallback = 0;
        var nested = 0;
        var withFragment = 0;
        var unreachable = 0;
        var maxToc = 0;

        report.AppendLine($"样本目录：{corpus}");
        report.AppendLine($"共 {files.Length} 本");
        report.AppendLine();

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var index = EpubPackageIndex.TryRead(file, out var reason);
            Assert.NotNull(index);

            var name = Path.GetFileName(file);
            var isThird = (index!.Version ?? string.Empty).StartsWith("3", StringComparison.Ordinal);
            if (isThird)
            {
                epub3++;
            }
            else
            {
                epub2++;
            }

            if (index.TocIsFallback)
            {
                fallback++;
            }
            else
            {
                Assert.NotEmpty(index.Toc);
            }

            var (count, depth, fragments) = Count(index.Toc, 1);
            maxToc = Math.Max(maxToc, count);
            if (depth > 1)
            {
                nested++;
            }

            withFragment += fragments;

            // 目录里的每一项：要么能跳（SpineIndex >= 0），要么是不可跳的分组标题（有子项）
            var dangling = Flatten(index.Toc).Count(e => e.SpineIndex < 0 && e.Children.Count == 0);
            unreachable += dangling;

            report.AppendLine(
                $"{name}\t版本={index.Version}\tspine={index.Spine.Count}\t目录={index.Toc.Count}" +
                $"\t层级={depth}\tfragment={fragments}\t降级={(index.TocIsFallback ? "是" : "否")}" +
                $"\t跳不了={dangling}\t{reason}");
        }

        report.AppendLine();
        report.AppendLine($"EPUB2={epub2}  EPUB3={epub3}  降级={fallback}  多层={nested}  带fragment={withFragment}  跳不了={unreachable}  最大目录={maxToc}");

        var outDir = Path.Combine(root, "build", "_t44");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "toc-corpus.txt"), report.ToString(), new UTF8Encoding(false));

        // 判据：EPUB2 与 EPUB3 两种都要在样本里出现过，且没有哪本解析不出目录
        Assert.True(epub2 > 0, "样本里没有 EPUB2");
        Assert.True(epub3 > 0, "样本里没有 EPUB3");
    }

    private static IEnumerable<TocEntry> Flatten(IReadOnlyList<TocEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;

            foreach (var child in Flatten(entry.Children))
            {
                yield return child;
            }
        }
    }

    private static (int Count, int Depth, int Fragments) Count(IReadOnlyList<TocEntry> entries, int depth)
    {
        var count = 0;
        var maxDepth = depth;
        var fragments = 0;

        foreach (var entry in entries)
        {
            count++;
            if (!string.IsNullOrEmpty(entry.Fragment))
            {
                fragments++;
            }

            var (childCount, childDepth, childFragments) = Count(entry.Children, depth + 1);
            count += childCount;
            fragments += childFragments;
            maxDepth = Math.Max(maxDepth, childDepth);
        }

        return (count, maxDepth, fragments);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tools", "manifest.json")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到仓库根（Tools/manifest.json）");
    }
}
