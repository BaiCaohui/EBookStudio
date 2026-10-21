namespace EbookToolSuite.Core.Enums;

/// <summary>
/// 笔记类型（SQLite notes.type 列）。设计文档 5.2：0 高亮、1 批注。
/// 两者共用 notes 表：高亮只存 quote，批注还存 content。
/// </summary>
public enum NoteType
{
    /// <summary>高亮：位置 + 选中原文 + 颜色，没有用户输入。</summary>
    Highlight = 0,

    /// <summary>批注：高亮的一切 + 用户输入的内容。</summary>
    Annotation = 1,
}
