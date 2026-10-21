using System.Collections.Concurrent;
using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace EbookToolSuite.Infrastructure.Logging;

/// <summary>
/// 内存环形缓冲 sink：固定 5000 条，不增长，进程退出即丢。
/// 供调试面板（F12，阶段 7）与「复制最近 N 行日志」菜单用。
/// 它存在的理由：文件 sink 是异步的，出问题时日志可能还没落盘，
/// 而内存里已经有了。
/// </summary>
public sealed class LogRingBuffer : ILogEventSink
{
    public const int DefaultCapacity = 5000;

    public static LogRingBuffer Instance { get; } = new();

    private readonly LogEvent?[] _buffer;
    private readonly object _gate = new();
    private int _next;
    private int _count;

    public LogRingBuffer(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _buffer = new LogEvent?[capacity];
    }

    public int Capacity => _buffer.Length;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null)
        {
            return;
        }

        lock (_gate)
        {
            _buffer[_next] = logEvent;
            _next = (_next + 1) % _buffer.Length;

            if (_count < _buffer.Length)
            {
                _count++;
            }
        }
    }

    /// <summary>取最近 <paramref name="maxLines"/> 条，按时间从旧到新。</summary>
    public IReadOnlyList<string> Snapshot(int maxLines)
    {
        lock (_gate)
        {
            var take = Math.Clamp(maxLines, 0, _count);
            var result = new List<string>(take);
            var start = (_next - take + _buffer.Length * 2) % _buffer.Length;

            for (var i = 0; i < take; i++)
            {
                var item = _buffer[(start + i) % _buffer.Length];
                if (item is not null)
                {
                    result.Add(Format(item));
                }
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer);
            _next = 0;
            _count = 0;
        }
    }

    private static string Format(LogEvent e)
    {
        var sb = new StringBuilder();
        sb.Append(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append(" [").Append(LevelTag(e.Level)).Append("] ");
        sb.Append(e.RenderMessage());

        if (e.Exception is not null)
        {
            sb.AppendLine();
            sb.Append(e.Exception);
        }

        return sb.ToString();
    }

    private static string LevelTag(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "???",
    };
}