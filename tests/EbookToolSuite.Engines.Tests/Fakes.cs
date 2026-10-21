using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Interfaces;

namespace EbookToolSuite.Engines.Tests;

/// <summary>可控时钟，用来测缓存过期而不用真的等 24 小时。</summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; }
        = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(8));

    public void Advance(TimeSpan delta) => Now = Now.Add(delta);
}

/// <summary>
/// 假的进程宿主。记录被调用了几次、参数是什么，并返回预设结果。
/// 有了它，ToolLocator 的缓存逻辑可以完全脱离真实进程来测。
/// </summary>
internal sealed class FakeProcessHost : IProcessHost
{
    private readonly Func<ProcessSpec, ProcessResult> _responder;

    public FakeProcessHost(Func<ProcessSpec, ProcessResult> responder)
        => _responder = responder;

    /// <summary>只回固定输出的便捷构造。</summary>
    public FakeProcessHost(string stdout, int exitCode = 0)
        : this(_ => new ProcessResult(exitCode, stdout, string.Empty,
                                     TimeSpan.FromMilliseconds(10)))
    {
    }

    public int CallCount { get; private set; }

    public List<ProcessSpec> Calls { get; } = new();

    public Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        IProgress<ProcessOutputLine>? output = null,
        CancellationToken ct = default)
    {
        CallCount++;
        Calls.Add(spec);
        return Task.FromResult(_responder(spec));
    }
}