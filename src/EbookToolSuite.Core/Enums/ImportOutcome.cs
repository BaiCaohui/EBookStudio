namespace EbookToolSuite.Core.Enums;

/// <summary>
/// 单本导入的结果分类。UI 按这个决定提示文案与可用动作（设计文档 3.7 错误码到 UI 映射）。
/// 用枚举而不是异常：批量导入时一本失败不能拖垮整批（设计文档 7.1.1）。
/// </summary>
public enum ImportOutcome
{
    /// <summary>导入成功，书已写库且文件已落到 Data/Library 正式区。</summary>
    Imported = 0,

    /// <summary>库中已有相同快速指纹的文件，本次未导入。BookId 指向已存在的那本书。</summary>
    Duplicate = 1,

    /// <summary>不是有效的 EPUB（zip 损坏、缺 OPF、内容读不出）。未写库、未复制文件。</summary>
    InvalidEpub = 2,

    /// <summary>检测到 DRM 加密（META-INF/encryption.xml）。未写库、未复制文件。</summary>
    DrmProtected = 3,

    /// <summary>扩展名不在导入白名单内。</summary>
    UnsupportedFormat = 4,

    /// <summary>源文件不存在、不是文件或不可读。</summary>
    SourceUnreadable = 5,

    /// <summary>用户取消。中间产物必须已清干净。</summary>
    Canceled = 6,

    /// <summary>其他失败：磁盘满、权限、数据库错误等。见 Result.Error。</summary>
    Failed = 7,
}