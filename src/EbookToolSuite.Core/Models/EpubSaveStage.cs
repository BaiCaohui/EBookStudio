namespace EbookToolSuite.Core.Models;

/// <summary>
/// 「已经打好包并校验完」、但**还没有替换原文件**时的中间状态。
///
/// 为什么要有这个东西：EpubCheck 要跑 3-15 秒，而且结果可能要问用户
/// （有错误问「仍然保存」，超时问「跳过校验」）。所以保存被拆成两段：
///   第一段 Stage：打包到暂存文件 + 校验 -> 原文件一个字节都没动；
///   第二段 Commit：用户拍板后再把暂存文件原子替换过去。
/// 这样「校验没过」或「用户取消」时，原文件天然是完好的。
/// </summary>
public sealed record EpubSaveStage(
    string TargetPath,
    string StagedPath,
    EpubPackageResult Package,
    ValidationDecision Decision);
