using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 编辑书库里已有的 CBZ：解压到临时工作区 → 交给编辑窗改 → 重新打包覆盖原文件。
/// </summary>
/// <remarks>
/// 与 <see cref="ICbzPacker"/> 的分工：那个只管「把一个文件夹/一组条目打成 zip」；
/// 这个负责「现有 CBZ 的往返」——解压、读旧元数据、备份原件、原子替换。
/// 只支持 CBZ（zip）；CBR 是 rar，重新打包会变成 zip，等于偷偷换了格式。
/// </remarks>
public interface ICbzEditService
{
    /// <summary>
    /// 解压 <paramref name="cbzPath"/> 里的图片到临时工作区，并读出包内 ComicInfo.xml。
    /// </summary>
    /// <remarks>
    /// 图片顺序 = 包内文件名自然序（阅读器看到的就是这个顺序）。
    /// 包内子目录会被拍平（编辑窗与打包器都按「文件名」表达顺序，不支持子路径）。
    /// 打不开 / 一张图片都没有时抛 <see cref="Exceptions.AppException"/>，并清掉临时目录。
    /// </remarks>
    Task<CbzEditWorkspace> OpenAsync(string cbzPath, CancellationToken ct = default);

    /// <summary>
    /// 按给定顺序与元数据重新打包，**先备份原文件**，再原子替换 <see cref="CbzEditWorkspace.CbzPath"/>。
    /// </summary>
    /// <returns>新文件体积与图片数（调用方据此更新 book_formats 与封面缓存）。</returns>
    Task<CbzEditSaveResult> SaveAsync(
        CbzEditWorkspace workspace,
        IReadOnlyList<CbzPackEntry> entries,
        ComicMetadata metadata,
        CancellationToken ct = default);

    /// <summary>删除临时工作区。关窗/失败/保存完成后都要调用（幂等，不抛异常）。</summary>
    void Cleanup(CbzEditWorkspace workspace);
}
