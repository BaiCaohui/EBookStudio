namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 时间来源。注入它才能测"缓存 24 小时后过期"这类逻辑，
/// 否则测试只能真的等 24 小时或者去改系统时间。
/// </summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}