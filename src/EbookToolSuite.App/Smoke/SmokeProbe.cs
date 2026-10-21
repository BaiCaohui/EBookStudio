using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Config;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// 冒烟脚本共用的几个探针：Calibre 残留进程、Calibre 运行目录残留、同步进度。
/// </summary>
/// <remarks>
/// 抽出来是因为 T1-4（转换冒烟）与 T1-7（VS1 验收）要用同一套判据。
/// 两处各写一份的话，判据一旦不一致，就会出现"一边绿一边红"而没人发现。
/// </remarks>
internal static class SmokeProbe
{
    /// <summary>Calibre 会拉起来的进程名。</summary>
    public static readonly string[] CalibreProcessNames = { "ebook-convert", "python", "pythonw" };

    /// <summary>
    /// 当前有几个"属于我们这套工具"的进程还活着（判孤儿用）。
    /// 只认 MainModule 路径落在 Tools 目录下的，别人机器上的同名进程不算。
    /// </summary>
    public static int CountToolProcesses(AppPaths paths)
    {
        var count = 0;

        foreach (var name in CalibreProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var file = process.MainModule?.FileName;

                    if (file is not null
                        && file.StartsWith(paths.ToolsRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }
                }
                catch (Exception)
                {
                    // 别人的进程读不到 MainModule，不算我们的。
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return count;
    }

    /// <summary>
    /// 用一份真实 EPUB 当模板克隆出一本：zip 条目原样复制，再多塞一个带序号的标记文件。
    /// 这样每本都是合法 EPUB，且文件大小与内容都不同，快速指纹互不相同。
    /// （直接复制同一份文件 N 次会被判成 N 本重复，那样就测不到导入了。）
    /// </summary>
    public static void CloneWithMarker(string templatePath, string targetPath, int marker)
    {
        using var template = ZipFile.OpenRead(templatePath);
        using var output = File.Create(targetPath);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);

        foreach (var entry in template.Entries)
        {
            // mimetype 必须是第一条且不压缩，否则 EPUB 直接不合法（设计文档 6.1.5 铁律）。
            var level = entry.FullName == "mimetype"
                ? CompressionLevel.NoCompression
                : CompressionLevel.Optimal;

            var created = zip.CreateEntry(entry.FullName, level);
            using var source = entry.Open();
            using var destination = created.Open();
            source.CopyTo(destination);
        }

        var markerEntry = zip.CreateEntry("META-INF/ets-smoke-" + marker + ".txt", CompressionLevel.Optimal);
        using var writer = markerEntry.Open();
        var payload = Encoding.UTF8.GetBytes("marker " + marker + " " + new string((char)109, (marker % 127) + 1));
        writer.Write(payload, 0, payload.Length);
    }

    /// <summary>
    /// 造一批可导入的样本：按 5:3:2 分散在根目录 / sub1 / sub2\deep 三层，
    /// 顺便放一个坏 EPUB 与一个非电子书文件（withNoise 打开时），
    /// 用来验"坏书不卡住队列"与"非电子书被跳过"。
    /// </summary>
    public static void BuildCorpus(
        string templateEpub, string root, int goodCount, int markerBase, bool withNoise = true)
    {
        for (var i = 0; i < goodCount; i++)
        {
            var relative = i < goodCount / 2
                ? string.Empty
                : i < goodCount * 8 / 10 ? "sub1" : Path.Combine("sub2", "deep");

            var dir = relative.Length == 0 ? root : Path.Combine(root, relative);
            Directory.CreateDirectory(dir);

            CloneWithMarker(templateEpub, Path.Combine(dir, "book-" + i.ToString("D3") + ".epub"), markerBase + i);
        }

        if (!withNoise)
        {
            return;
        }

        // 故意取一个排在最前面的名字（"aaa-"）：如果队列被坏书卡死，后面那批一本都进不来。
        File.WriteAllBytes(Path.Combine(root, "aaa-broken.epub"), RandomBytes(4096));

        // 非电子书文件：扫描阶段就该被跳过，不该出现在队列里。
        File.WriteAllBytes(Path.Combine(root, "notes.jpg"), RandomBytes(64));
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }
    /// <summary>临时目录里还剩几个 Calibre 运行目录（判清理用）。</summary>
    public static int CountLeftoverRunDirectories(AppPaths paths)
    {
        var root = Path.Combine(paths.TempDir, "calibre");

        return Directory.Exists(root)
            ? Directory.GetDirectories(root, "conv-*").Length
            : 0;
    }

    /// <summary>
    /// 同步执行的 IProgress：Progress&lt;T&gt; 会派发到线程池，
    /// 冒烟里"等到进程真的开始产出"这类时序判断就不可靠了。
    /// </summary>
    public sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
    /// <summary>不真的打开任何东西的 IShellLauncher：冒烟构造页面时用，免得弹资源管理器。</summary>
    internal sealed class NoopShellLauncher : IShellLauncher
    {
        public void OpenFolder(string path)
        {
        }

        public void RevealInExplorer(string filePath)
        {
        }

        public void OpenFile(string path)
        {
        }

        public void OpenUrl(string url)
        {
        }
    }
}