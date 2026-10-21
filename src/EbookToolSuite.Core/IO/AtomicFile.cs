using System.Text;

namespace EbookToolSuite.Core.IO;

/// <summary>
/// 原子写文件（执行计划 T7-2 判据 4：「所有写文件操作都是临时文件 + 原子替换」）。
///
/// 【为什么必须这样】直接 <c>File.WriteAllText(最终路径)</c> 时，断电、杀进程、
/// 磁盘满都会留下一个"半截文件"：JSON 解析不了、章节 XHTML 被截断、书库以为文件完好。
/// 这里统一成：写同目录的 <c>*.part</c> -> flush 到磁盘 -> <see cref="File.Move(string,string,bool)"/>
/// 原子改名。改名是同一卷内的元数据操作，要么旧文件还在，要么新文件完整，不存在中间态。
///
/// 【为什么放在 Core】Engines / Infrastructure / Services 三层都要用；
/// 只依赖 BCL，不引入任何第三方包，符合 Core「零外部依赖」的约束。
/// </summary>
public static class AtomicFile
{
    /// <summary>临时文件后缀。启动清理与打包器都会跳过 <c>*.part</c>。</summary>
    public const string TempSuffix = ".part";

    public static void WriteAllText(string path, string content, Encoding encoding)
        => WriteAllBytes(path, encoding.GetBytes(content));

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(bytes);

        var temp = path + TempSuffix;

        try
        {
            using (var stream = new FileStream(
                       temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>写入一段由回调产生的字节流（XmlWriter / 复制流用）。</summary>
    public static void Write(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(write);

        var temp = path + TempSuffix;

        try
        {
            using (var stream = new FileStream(
                       temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>复制一个文件到目标路径（先写到目标旁的临时名，再原子改名）。</summary>
    public static void Copy(string source, string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(target);

        var temp = target + TempSuffix;

        try
        {
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>异步版本。用于导出这类可能写入较大文本、又不想卡 UI 线程的场景。</summary>
    public static async Task WriteAllTextAsync(
        string path,
        string content,
        Encoding encoding,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);

        var temp = path + TempSuffix;

        try
        {
            await using (var stream = new FileStream(
                             temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = encoding.GetBytes(content);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理失败只留下一个 .part，启动清理会扫掉；不改变原始异常
        }
    }
}
