using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 预检发现重复后由谁决定怎么办（执行计划 T2-1："15 本重复书只弹一次对话框"）。
/// 抽成接口是为了让冒烟测试能注入一个"脚本化的决定者"并**断言它只被问了一次**
///  对话框本身没法在无头环境里点按钮，但"被问了几次"是能精确验证的。
/// </summary>
public interface IDuplicateResolver
{
    /// <summary>
    /// 整批重复一次性决策。实现应当弹**一个**对话框（三个按钮），不是每本一个。
    /// 返回 SkipAll / ImportAll / AskEach。
    /// </summary>
    Task<DuplicatePolicy> ResolveBatchAsync(
        IReadOnlyList<ImportJob> duplicates, CancellationToken ct = default);

    /// <summary>
    /// 仅当 ResolveBatchAsync 返回 AskEach 时才会被调用：对这一本单独问一次。
    /// 返回 true 表示"这本仍要导入"。
    /// </summary>
    Task<DuplicatePolicy> ResolveSingleAsync(
        ImportJob duplicate, int index, int total, CancellationToken ct = default);
}