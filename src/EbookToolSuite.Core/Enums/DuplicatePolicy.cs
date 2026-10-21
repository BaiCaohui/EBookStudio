namespace EbookToolSuite.Core.Enums;

/// <summary>
/// 导入前批量预检发现重复时，用户对整批重复项的一次性决策
/// （执行计划 T2-1：15 本重复只弹一次对话框，绝不逐本弹）。
/// </summary>
public enum DuplicatePolicy
{
    /// <summary>[跳过全部重复]。重复的书不入库，但会记进 import_batch.note。</summary>
    SkipAll = 0,

    /// <summary>[全部仍要导入]。当作全新的一本书，照常导入一份。</summary>
    ImportAll = 1,

    /// <summary>[逐项决定]。对每一本重复单独问一次，用户可混合选择。</summary>
AskEach = 2,

      /// <summary>[替换书籍]。把库里重复的那本替换成本次导入的文件（同一本书只留一本）。</summary>
      Replace = 3,
}