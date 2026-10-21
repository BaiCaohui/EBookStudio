namespace EbookToolSuite.Core.Models;

/// <summary>
/// 编辑现有 CBZ 时解压出来的临时工作区：图片文件 + 包内书籍信息。
/// </summary>
/// <remarks>
/// <para>
/// 【为什么先解压】用户要求「编辑书库里的 CBZ」= 把现有 CBZ 解压 → 在编辑窗里改元数据/图片顺序 →
/// 重新打包覆盖原文件。编辑窗本身只认「一个图片文件夹 + 一份元数据」，
/// 所以这条链路的第一步就是把 CBZ 摊成它认识的样子。
/// </para>
/// <para>
/// 【只在临时目录】<see cref="WorkspaceDir"/> 落在 <c>Data/Workspaces/</c> 下，
/// 用完由 <c>ICbzEditService.Cleanup</c> 删掉；原 CBZ 在保存之前一个字节都不动。
/// </para>
/// </remarks>
public sealed class CbzEditWorkspace
{
    /// <summary>被编辑的 CBZ 绝对路径（保存时就地替换这个文件）。</summary>
    public required string CbzPath { get; init; }

    /// <summary>临时工作区根目录（清理时整个删掉）。</summary>
    public required string WorkspaceDir { get; init; }

    /// <summary>解压出来的图片所在目录（编辑窗显示用）。</summary>
    public required string Folder { get; init; }

    /// <summary>解压出来的图片绝对路径，顺序 = 包内文件名自然序（= 阅读顺序）。</summary>
    public required IReadOnlyList<string> Images { get; init; }

    /// <summary>原包内 <c>ComicInfo.xml</c> 解析出的书籍信息；没有就是空对象。</summary>
    public required ComicMetadata Metadata { get; init; }
}

/// <summary>编辑 CBZ 并回写原文件的结果。</summary>
/// <param name="OutputPath">被替换的 CBZ 绝对路径。</param>
/// <param name="ImageCount">重新打包后包里的图片数。</param>
/// <param name="SizeBytes">替换后新文件的字节数。</param>
/// <param name="Duration">打包耗时。</param>
public sealed record CbzEditSaveResult(
    string OutputPath,
    int ImageCount,
    long SizeBytes,
    TimeSpan Duration);
