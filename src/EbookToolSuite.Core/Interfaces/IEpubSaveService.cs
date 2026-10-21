using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 编辑保存的两段式流程（T3-5）：先打包到暂存文件并校验，用户拍板后再原子替换。
/// </summary>
public interface IEpubSaveService
{
    /// <summary>
    /// 第一段：把工作区打包到 <c>{目标文件}.ets-stage</c>，然后校验它。
    /// 全程不碰目标文件；校验的结论放在 <see cref="EpubSaveStage.Decision"/> 里。
    /// </summary>
    Task<EpubSaveStage> StageAsync(
        string workDir,
        string targetEpubPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// 第二段：把暂存产物原子替换到目标文件（用户选了「仍然保存」或校验通过时调用）。
    ///
    /// 【T3-6 的顺序要求】给了 <paramref name="backup"/> 时，**先**把目标文件备份成还原点
    /// （<c>Data/Backup/RestorePoints/{pointId}/</c>），**再**做原子替换；替换失败会按备份
    /// 把目标恢复原样。备份失败就直接中止，绝不"没有备份就替换"。
    ///
    /// 目标被占用时抛 FileLocked，此时原文件与暂存文件都还在（还原点也留着）。
    /// </summary>
    /// <param name="stage">第一段产出的暂存产物。</param>
    /// <param name="backup">要备份的原文件（bookId/formatId/路径）；null = 不建还原点。</param>
    /// <param name="ct">取消令牌。</param>
    Task<EpubPackageResult> CommitAsync(
        EpubSaveStage stage,
        RestorePointRequest? backup = null,
        CancellationToken ct = default);

    /// <summary>丢掉暂存产物（用户取消 / 关窗）。</summary>
    void Discard(EpubSaveStage stage);
}
