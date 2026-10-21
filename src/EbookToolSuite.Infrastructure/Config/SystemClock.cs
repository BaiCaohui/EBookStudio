using EbookToolSuite.Core.Interfaces;

namespace EbookToolSuite.Infrastructure.Config;

/// <summary>生产环境的时钟。测试里用 FakeClock 替身。</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset Now => DateTimeOffset.Now;
}