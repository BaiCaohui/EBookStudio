namespace EbookToolSuite.Core.Common;

/// <summary>
/// 库里所有时间列统一是 Unix 毫秒（UTC），读写都过这里，
/// 禁止在别处手写 ToUnixTimeMilliseconds。
/// </summary>
public static class UnixTime
{
    public static long ToMillis(DateTimeOffset value)
        => value.ToUnixTimeMilliseconds();

    public static long? ToMillis(DateTimeOffset? value)
        => value?.ToUnixTimeMilliseconds();

    public static DateTimeOffset FromMillis(long millis)
        => DateTimeOffset.FromUnixTimeMilliseconds(millis);

    public static DateTimeOffset? FromMillis(long? millis)
        => millis is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(millis.Value);

    /// <summary>published_date 在库里是 TEXT，统一 yyyy-MM-dd。</summary>
    public static string? ToDateText(DateOnly? value)
        => value?.ToString("yyyy-MM-dd");

    public static DateOnly? DateFromText(string? text)
        => DateOnly.TryParse(text, out var d) ? d : null;
}