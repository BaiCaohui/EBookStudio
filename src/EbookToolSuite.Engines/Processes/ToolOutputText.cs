using System.Text;
using System.Text.RegularExpressions;

namespace EbookToolSuite.Engines.Processes;

/// <summary>
/// 外部工具输出的乱码兜底（执行计划 T7-2 第 11 项）。
///
/// Calibre 已强制 <c>PYTHONIOENCODING=utf-8</c> 且 <c>CALIBRE_OVERRIDE_LANG=en</c>，
/// 正常情况下不会乱码；但工具可能被替换、包装脚本可能往管道里写本机代码页的字节，
/// 那时 <see cref="Encoding.UTF8"/> 会解出一串 U+FFFD（�）。
/// 这一层把控制字符剔掉、把连续的替换符收成一个明确的中文标记，
/// 保证界面上不会出现"一屏问号"。
/// </summary>
public static partial class ToolOutputText
{
    public const string EncodingFailureMark = "[输出编码异常]";

    /// <summary>清洗一行（或一段）外部工具输出。空值原样返回。</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // 控制字符：保留 \t；换行由调用方按行处理，不在这里保留。
        var withoutControls = ControlChars().Replace(text, string.Empty);

        // 解不出来的字节在 UTF-8 解码后是 U+FFFD，连续多个收成一个标记。
        return ReplacementRuns().Replace(withoutControls, EncodingFailureMark);
    }

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")]
    private static partial Regex ControlChars();

    [GeneratedRegex("\uFFFD+")]
    private static partial Regex ReplacementRuns();
}
