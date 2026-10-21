namespace EbookToolSuite.Core.Enums;

/// <summary>阅读状态，存库为 INTEGER（books.reading_status）。</summary>
public enum ReadingStatus
{
    Unread = 0,
    Reading = 1,
    Finished = 2,
    Abandoned = 3,
}