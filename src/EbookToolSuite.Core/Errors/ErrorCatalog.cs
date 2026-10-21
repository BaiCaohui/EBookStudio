using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Errors;

/// <summary>
/// 一个错误码在界面上该怎么说话：中文文案（发生了什么 + 为什么）+ 建议动作（怎么办）。
/// </summary>
/// <param name="Code">应用内错误码。</param>
/// <param name="EtsCode">对外展示的错误码（设计文档附录 A：ETS-域-四位序号）。</param>
/// <param name="Message">面向用户的中文提示。禁止放英文异常原文或堆栈。</param>
/// <param name="Action">建议动作：用户下一步能做什么。</param>
public sealed record ErrorInfo(ErrorCode Code, string EtsCode, string Message, string Action);

/// <summary>
/// 错误码目录（执行计划 T7-2 判据 3）。
///
/// 【为什么需要它】异常一路抛到 UI 时，调用方只拿到一个 <see cref="ErrorCode"/>。
/// 如果每个弹窗自己现编文案，同一个错误在不同页面会说法不一，还会有人顺手把
/// <c>ex.Message</c>（可能是英文）贴上去。这个目录是**唯一**的「错误码 -> 人话」来源。
///
/// 【覆盖范围】<see cref="ErrorCode"/> 的每一个取值都必须有一条。单测
/// <c>ErrorCatalogTests</c> 会按枚举逐个核对，漏一个就红。
/// </summary>
public static class ErrorCatalog
{
    private static readonly IReadOnlyDictionary<ErrorCode, ErrorInfo> Entries = Build();

    /// <summary>全部条目，按错误码排序。测试与诊断页用。</summary>
    public static IReadOnlyCollection<ErrorInfo> All { get; } =
        Entries.Values.OrderBy(i => (int)i.Code).ToArray();

    /// <summary>取一个错误码的文案。未登记的码按 Unknown 处理，绝不返回 null。</summary>
    public static ErrorInfo Get(ErrorCode code)
        => Entries.TryGetValue(code, out var info) ? info : Entries[ErrorCode.Unknown];

    /// <summary>错误码是否已登记（单测与诊断用）。</summary>
    public static bool Contains(ErrorCode code) => Entries.ContainsKey(code);

    private static IReadOnlyDictionary<ErrorCode, ErrorInfo> Build()
    {
        var list = new ErrorInfo[]
        {
            // ---------- 通用 ----------
            new(ErrorCode.Unknown, "ETS-GEN-0001",
                "程序遇到意外错误，但会继续运行。",
                "复制诊断信息并反馈；详细堆栈已写入日志。"),
            new(ErrorCode.InternalError, "ETS-GEN-0001",
                "程序遇到意外错误。",
                "复制诊断信息并反馈；详细堆栈已写入日志。"),
            new(ErrorCode.NotImplemented, "ETS-GEN-0001",
                "这个功能还没有实现。",
                "换用其它入口，或等待后续版本。"),
            new(ErrorCode.Canceled, "ETS-GEN-0002",
                "操作已取消。",
                "需要的话重新执行。"),
            new(ErrorCode.DiskFull, "ETS-GEN-0005",
                "磁盘空间不足。",
                "清理磁盘，或把数据目录 / 输出位置换到空间更大的盘。"),
            new(ErrorCode.FileLocked, "ETS-GEN-0006",
                "文件正被其他程序占用。",
                "关闭占用它的程序（如阅读器或杀毒软件）后重试。"),
            new(ErrorCode.InvalidPath, "ETS-GEN-0007",
                "路径无效或过长。",
                "检查路径；启用 Windows 长路径支持后重试。"),
            new(ErrorCode.AccessDenied, "ETS-GEN-0008",
                "没有权限访问这个位置。",
                "换一个可写目录；避免用管理员身份运行。"),
            new(ErrorCode.EncodingError, "ETS-GEN-0003",
                "无法识别文本文件的编码。",
                "手动指定编码（UTF-8 / GBK / GB18030）后重试。"),

            // ---------- 书库 ----------
            new(ErrorCode.FileNotFound, "ETS-LIB-0103",
                "文件不存在或已被移动。",
                "重新定位文件，或从书库移除这条记录。"),
            new(ErrorCode.UnsupportedFormat, "ETS-LIB-0102",
                "不支持这种文件格式。",
                "查看支持的格式清单，或先转换成 EPUB / TXT / PDF / MOBI / CBZ。"),
            new(ErrorCode.DuplicateBook, "ETS-LIB-0104",
                "这本书已经在库中。",
                "选择「并入现有书」或「作为新书导入」。"),
            new(ErrorCode.DrmDetected, "ETS-LIB-0105",
                "这本书带 DRM 保护，本工具不处理。",
                "请先用外部工具得到无 DRM 的版本，再导入。"),

            // ---------- 编辑 / 打包 ----------
            new(ErrorCode.CorruptFile, "ETS-EDIT-0301",
                "文件损坏或结构不完整。",
                "从备份还原，或重新获取这个文件。"),
            new(ErrorCode.EpubInvalid, "ETS-EDIT-0301",
                "不是有效的 EPUB 文件。",
                "重新下载或换一个来源；也可以尝试「一键修复」。"),
            new(ErrorCode.UnsafeArchiveEntry, "ETS-EDIT-0301",
                "压缩包里有非法路径（可能被恶意构造），已拒绝处理。",
                "重新获取这个文件；不要打开来源不明的压缩包。"),
            new(ErrorCode.ValidationFailed, "ETS-EDIT-0306",
                "内容没通过校验。",
                "打开校验面板定位问题；修复后重试。"),
            new(ErrorCode.CbzPackFailed, "ETS-EDIT-0305",
                "把图片打包成 CBZ 失败。",
                "检查图片文件夹与输出目录后重试。"),
            new(ErrorCode.OutputStreamNotSeekable, "ETS-EDIT-0305",
                "内部错误：输出流不可用。",
                "重试；若反复出现请反馈。"),
            new(ErrorCode.PackageMimetypeNotFirst, "ETS-EDIT-0305",
                "打包结果不合法（mimetype 不是第一条）。",
                "重试；若反复出现请反馈。"),
            new(ErrorCode.PackageMimetypeNotStored, "ETS-EDIT-0305",
                "打包结果不合法（mimetype 被压缩）。",
                "重试；若反复出现请反馈。"),
            new(ErrorCode.PackageMimetypeContentInvalid, "ETS-EDIT-0305",
                "打包结果不合法（mimetype 内容不对）。",
                "重试；若反复出现请反馈。"),
            new(ErrorCode.PackageMimetypeDuplicated, "ETS-EDIT-0305",
                "打包结果不合法（mimetype 重复）。",
                "重试；若反复出现请反馈。"),

            // ---------- 转换 ----------
            new(ErrorCode.ProcessCrashed, "ETS-CONV-0203",
                "外部程序异常退出。",
                "查看日志尾部；重试。"),
            new(ErrorCode.ConversionFailed, "ETS-CONV-0203",
                "转换失败。",
                "查看日志尾部；重试。"),
            new(ErrorCode.ProcessTimeout, "ETS-CONV-0204",
                "操作超时。",
                "重试；或在设置里增大超时时间。"),
            new(ErrorCode.ConversionNoOutput, "ETS-CONV-0205",
                "转换没有产出有效文件。",
                "查看日志；重试。"),
            new(ErrorCode.ConversionOutputExists, "ETS-CONV-0206",
                "目标格式已存在。",
                "选择覆盖、跳过，或先删除旧格式。"),
            new(ErrorCode.ConversionUnsupported, "ETS-CONV-0207",
                "该转换链路不受支持。",
                "换一种源格式或目标格式。"),
            new(ErrorCode.ConversionArgsRejected, "ETS-CONV-0202",
                "转换参数不被当前 Calibre 版本支持。",
                "恢复默认参数，或升级 Calibre。"),
            new(ErrorCode.ConversionCanceled, "ETS-GEN-0002",
                "转换已取消。",
                "需要的话重新加入队列。"),

            // ---------- 阅读 ----------
            new(ErrorCode.ReaderOpenFailed, "ETS-READ-0401",
                "无法打开这本书。",
                "检查文件是否完整；查看详情，或换用外部程序打开。"),
            new(ErrorCode.ReaderRenderFailed, "ETS-READ-0402",
                "内容渲染失败。",
                "重试；或换用外部程序打开。"),
            new(ErrorCode.WebMessageTimeout, "ETS-READ-0404",
                "阅读器无响应。",
                "刷新阅读器。"),

            // ---------- 工具链 ----------
            new(ErrorCode.MissingTool, "ETS-TOOL-0501",
                "缺少必需的外部工具。",
                "在「设置 - 工具」里指定路径，或按准备说明把工具放回 Tools/ 目录。"),
            new(ErrorCode.JavaMissing, "ETS-TOOL-0501",
                "没找到 Java 运行环境（JRE），EpubCheck 校验不可用。",
                "在设置里指定 JRE 路径，或选择「未校验保存」。"),
            new(ErrorCode.ToolVersionMismatch, "ETS-TOOL-0502",
                "外部工具版本不在支持范围内。",
                "按文档更换为支持版本。"),
            new(ErrorCode.ToolProbeFailed, "ETS-TOOL-0503",
                "外部工具无法启动。",
                "检查路径和运行库依赖；重新检测工具。"),

            // ---------- 数据 / 存储 ----------
            new(ErrorCode.DataDirNotWritable, "ETS-DATA-0605",
                "数据目录不可写或所在磁盘不可用。",
                "检查磁盘是否已连接；或在「设置 - 高级 - 数据目录」换一个可写目录。"),
        };

        var map = new Dictionary<ErrorCode, ErrorInfo>();
        foreach (var info in list)
        {
            map[info.Code] = info;
        }

        return map;
    }
}
