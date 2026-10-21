namespace EbookToolSuite.Core.Enums;

/// <summary>
/// 全应用统一错误码。每一类都要在 UI 层配一个「用户提示 + 动作按钮」，
/// 见设计文档 3.7 的错误码到 UI 映射表。不允许只抛错误码不给动作。
/// </summary>
public enum ErrorCode
{
    Unknown = 0,

    // ---------- 用户可修复 ----------
    FileLocked,
    FileNotFound,
    AccessDenied,
    DiskFull,
    InvalidPath,
    EncodingError,
    DrmDetected,
    UnsupportedFormat,
    CorruptFile,

    /// <summary>
    /// 不是有效的 EPUB：缺 META-INF/container.xml、OPF 读不出、zip 结构损坏、
    /// 或者根本不是 zip。与 CorruptFile 的区别是：这个专指 EPUB 解析失败，
    /// UI 提示「不是有效的 EPUB 文件」并建议重新下载。
    /// </summary>
    EpubInvalid,

    /// <summary>
    /// 压缩包内含不安全路径（Zip Slip：.. 逃逸、绝对路径、盘符、符号链接）。
    /// UI 提示「压缩包有非法路径」并建议重新下载；这种包一个字节都不许落盘。
    /// </summary>
    UnsafeArchiveEntry,

    // ---------- 环境问题 ----------
    MissingTool,
    ToolVersionMismatch,
    ToolProbeFailed,
    JavaMissing,

    /// <summary>
    /// 没有任何一个候选数据目录可写（程序目录 Data/ 只读、
    /// 用户目录也不可用）。UI 应提供「选择另一个目录」与「退出」。
    /// </summary>
    DataDirNotWritable,

    // ---------- 流程 / 质量 ----------
    ValidationFailed,
    ProcessCrashed,
    ProcessTimeout,
    Canceled,
    DuplicateBook,

    // ---------- 转换 ----------

    /// <summary>该转换链路不受支持。UI 应给出支持的链路清单。</summary>
    ConversionUnsupported,

    /// <summary>参数不在当前 Calibre 版本的白名单里，执行前就拦下。</summary>
    ConversionArgsRejected,

    /// <summary>转换进程退出码非 0（1=运行期错误，2=参数错误）。</summary>
    ConversionFailed,

    /// <summary>退出码是 0，但没产出文件或产物是 0 字节。</summary>
    ConversionNoOutput,

    /// <summary>用户在转换过程中点了取消。</summary>
    ConversionCanceled,

    /// <summary>这本书已经有该格式了，且不是这次产出的。UI 应提供"删除旧格式"或"换个格式"。</summary>
    ConversionOutputExists,

    /// <summary>
    /// 把图片文件夹打成 CBZ 失败：文件夹里没有图片、输出目录不可写、
    /// 或者打包过程中 IO 出错。UI 应提示具体原因并保留原文件夹。
    /// </summary>
    CbzPackFailed,

    // ---------- 打包（T3-4，错误码表见设计文档 3.7 扩展） ----------

    /// <summary>
    /// EPUB 输出流不可 seek。此时 ZipArchive 会给每个条目都带上 data descriptor（GPF bit3），
    /// 产出的包不合规。UI 提示「内部错误：输出流不可用」，动作按钮「重试」。
    /// </summary>
    OutputStreamNotSeekable,

    /// <summary>
    /// 打包结果的 mimetype 不是 ZIP 的第一条。老阅读器会直接拒收。
    /// UI 提示「打包结果不合法」，动作按钮「重试」。
    /// </summary>
    PackageMimetypeNotFirst,

    /// <summary>
    /// mimetype 被压缩了（OCF 要求必须是 STORED 存储方式）。UI 同 PackageMimetypeNotFirst。
    /// </summary>
    PackageMimetypeNotStored,

    /// <summary>
    /// mimetype 内容不是精确的 application/epub+zip（多了 BOM、换行或别的字节）。
    /// UI 提示「打包结果不合法」，动作按钮「重试」。
    /// </summary>
    PackageMimetypeContentInvalid,

    /// <summary>
    /// ZIP 里出现了多个 mimetype 条目（工作区里的 mimetype 没被排除的典型症状）。
    /// UI 提示「打包结果不合法」，动作按钮「重试」。
    /// </summary>
    PackageMimetypeDuplicated,

    // ---------- 阅读器（错误码表 ETS-READ-04xx） ----------

    /// <summary>ETS-READ-0401 无法打开书籍。文件缺失、格式不支持、缓存建不起来。</summary>
    ReaderOpenFailed,

    /// <summary>ETS-READ-0402 渲染失败。WebView2 报错或章节物化失败。</summary>
    ReaderRenderFailed,

    /// <summary>ETS-READ-0404 与阅读器的消息往返超时（默认 5 秒）。</summary>
    WebMessageTimeout,

    // ---------- 程序缺陷（应上报到日志） ----------
    InternalError,
    NotImplemented,
}
