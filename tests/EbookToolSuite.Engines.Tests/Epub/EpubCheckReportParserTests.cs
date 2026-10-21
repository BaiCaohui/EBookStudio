using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// EpubCheck 报告解析器。
///
/// 【为什么样本是真的】主用例吃 <c>Fixtures/epubcheck-sample-errors.json</c>那是
/// EpubCheck 5.4.0 对一本 58.5 KB 真实 EPUB 跑出来的报告，**原封不动**存进仓库的
/// （28061 字节、无 BOM、CRLF）。键名、大小写、嵌套结构一律以它为准：
/// T3-5 开工时我按记忆写过 locations 与 nUsage，那是错的教训，所以这里拒绝手写假结构。
/// 合成的那几份（干净 / 只有警告 / 坏 JSON）只用来覆盖真样本没有的分支，结构照真报告抄。
/// </summary>
public sealed class EpubCheckReportParserTests
{
    /// <summary>真报告：0 fatal / 6 error / 0 warning / 0 usage，全是 RSC-005。</summary>
    private static string RealFixture => Path.Combine(
        RepoRoot(),
        "tests",
        "EbookToolSuite.Engines.Tests",
        "Epub",
        "Fixtures",
        "epubcheck-sample-errors.json");

    // ---------- 真样本 ----------

    [Fact]
    public void 真报告_计数与_checker_段逐字一致()
    {
        var report = EpubCheckReportParser.ParseFile(RealFixture);

        Assert.False(report.ParseFailed);
        Assert.Equal(0, report.FatalCount);
        Assert.Equal(6, report.ErrorCount);
        Assert.Equal(0, report.WarningCount);
        Assert.Equal(0, report.UsageCount);
        Assert.Equal(6, report.Messages.Count);
        Assert.True(report.HasBlockingErrors);
        Assert.False(report.HasWarnings);
        Assert.Equal("fatal 0 / error 6 / warning 0", report.CountsText);
    }

    [Fact]
    public void 真报告_每条_message_的_ID_与严重级别都读对()
    {
        var report = EpubCheckReportParser.ParseFile(RealFixture);

        Assert.All(report.Messages, message =>
        {
            Assert.Equal("RSC-005", message.Id);
            // 真报告里是大写 ERROR，解析后必须归一成小写常量
            Assert.Equal(CheckSeverity.Error, message.Severity);
            Assert.True(message.IsError);
            Assert.False(string.IsNullOrWhiteSpace(message.Message));
        });
    }

    [Fact]
    public void 真报告_locations_的_path_line_column_逐字读对()
    {
        var report = EpubCheckReportParser.ParseFile(RealFixture);

        var first = report.Messages[0].FirstLocation;
        Assert.NotNull(first);
        Assert.Equal("text/part0000_split_001.html", first!.Path);
        Assert.Equal(14, first.Line);
        Assert.Equal(53, first.Column);
        Assert.Equal("text/part0000_split_001.html:14:53", first.DisplayText);

        // 位置数不能截断：真报告里这条 message 带 9 个位置，第二条带 13 个
        Assert.Equal(9, report.Messages[0].Locations.Count);
        Assert.Equal(13, report.Messages[1].Locations.Count);
    }

    [Fact]
    public void 真报告_前几条的展示文本可直接给人看()
    {
        var report = EpubCheckReportParser.ParseFile(RealFixture);

        var lines = report.TopMessages(3);

        Assert.Equal(3, lines.Count);
        Assert.StartsWith("[error] RSC-005 ", lines[0], StringComparison.Ordinal);
        Assert.Contains("text/part0000_split_001.html:14:53", lines[0], StringComparison.Ordinal);
    }

    // ---------- 干净 / 只有警告 ----------

    [Fact]
    public void 干净报告_没有消息也没有错误()
    {
        var report = EpubCheckReportParser.Parse(CleanJson);

        Assert.False(report.ParseFailed);
        Assert.Empty(report.Messages);
        Assert.Equal(0, report.IssueCount);
        Assert.False(report.HasBlockingErrors);
        Assert.False(report.HasWarnings);
    }

    [Fact]
    public void 只有警告_警告数读对且不算阻塞()
    {
        var report = EpubCheckReportParser.Parse(WarningJson);

        Assert.False(report.ParseFailed);
        Assert.Equal(1, report.WarningCount);
        Assert.True(report.HasWarnings);
        Assert.False(report.HasBlockingErrors);
        Assert.True(report.Messages[0].IsWarning);
        Assert.Equal("PKG-022", report.Messages[0].Id);
    }

    // ---------- 兜底：坏数据不许抛 ----------

    [Fact]
    public void 坏JSON_返回解析失败而不是抛异常()
    {
        var report = EpubCheckReportParser.Parse("{ 这根本不是 JSON ");

        Assert.True(report.ParseFailed);
        Assert.False(string.IsNullOrWhiteSpace(report.ParseFailureReason));
        Assert.Empty(report.Messages);
    }

    [Fact]
    public void 空报告_返回解析失败()
    {
        var report = EpubCheckReportParser.Parse("   ");

        Assert.True(report.ParseFailed);
        Assert.False(string.IsNullOrWhiteSpace(report.ParseFailureReason));
    }

    [Fact]
    public void 文件不存在_返回解析失败()
    {
        var report = EpubCheckReportParser.ParseFile(
            Path.Combine(Path.GetTempPath(), "ets-不存在的报告-" + Guid.NewGuid().ToString("N") + ".json"));

        Assert.True(report.ParseFailed);
        Assert.False(string.IsNullOrWhiteSpace(report.ParseFailureReason));
    }

    [Fact]
    public void 缺_checker_段_从_messages_自己数()
    {
        // 只有 messages、没有 checker：计数必须退化成从消息里数出来，不能全 0
        var report = EpubCheckReportParser.Parse("""
        {
          "messages": [
            { "ID": "OPF-003", "severity": "ERROR", "message": "x", "locations": [ { "path": "a.opf" } ] },
            { "ID": "OPF-014", "severity": "WARNING", "message": "y", "locations": [] },
            { "ID": "HTM-001", "severity": "INFO", "message": "z" }
          ]
        }
        """);

        Assert.False(report.ParseFailed);
        Assert.Equal(0, report.FatalCount);
        Assert.Equal(1, report.ErrorCount);
        Assert.Equal(1, report.WarningCount);
        Assert.Equal(1, report.UsageCount);
    }

    [Fact]
    public void 严重级别大小写混着写也能归一()
    {
        var report = EpubCheckReportParser.Parse("""
        {
          "messages": [
            { "ID": "A", "severity": "FATAL", "message": "1" },
            { "ID": "B", "severity": "error", "message": "2" },
            { "ID": "C", "severity": "Warning", "message": "3" },
            { "ID": "D", "severity": "USAGE", "message": "4" }
          ]
        }
        """);

        Assert.Equal(CheckSeverity.Fatal, report.Messages[0].Severity);
        Assert.Equal(CheckSeverity.Error, report.Messages[1].Severity);
        Assert.Equal(CheckSeverity.Warning, report.Messages[2].Severity);
        Assert.Equal(CheckSeverity.Usage, report.Messages[3].Severity);
    }

    [Fact]
    public void 位置缺行列_展示文本只给路径()
    {
        var report = EpubCheckReportParser.Parse("""
        {
          "messages": [
            { "ID": "X", "severity": "ERROR", "message": "m", "locations": [ { "path": "OEBPS/a.xhtml" } ] }
          ]
        }
        """);

        var location = report.Messages[0].FirstLocation!;
        Assert.Null(location.Line);
        Assert.Null(location.Column);
        Assert.Equal("OEBPS/a.xhtml", location.DisplayText);
    }

    // ---------- 合成样本：结构照真报告抄 ----------

    private const string CleanJson = """
    {
      "messages" : [ ],
      "customMessageFileName" : null,
      "checker" : {
        "path" : "book.epub",
        "filename" : "book.epub",
        "checkerVersion" : "5.4.0",
        "checkDate" : "10-08-2026 13:09:38",
        "elapsedTime" : 7000,
        "nFatal" : 0,
        "nError" : 0,
        "nWarning" : 0,
        "nUsage" : 0
      }
    }
    """;

    private const string WarningJson = """
    {
      "messages" : [ {
        "ID" : "PKG-022",
        "severity" : "WARNING",
        "message" : "Wrong file extension for image",
        "additionalLocations" : 0,
        "locations" : [ {
          "path" : "OEBPS/Images/cover.png",
          "line" : null,
          "column" : null,
          "context" : null
        } ],
        "suggestion" : null
      } ],
      "customMessageFileName" : null,
      "checker" : {
        "path" : "book.epub",
        "filename" : "book.epub",
        "checkerVersion" : "5.4.0",
        "checkDate" : "10-08-2026 13:09:38",
        "elapsedTime" : 7000,
        "nFatal" : 0,
        "nError" : 0,
        "nWarning" : 1,
        "nUsage" : 0
      }
    }
    """;

    /// <summary>从测试程序集目录往上找仓库根（靠 Tools/manifest.json 认）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tools", "manifest.json")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "没找到仓库根（一路往上都没看到 Tools/manifest.json）：" + AppContext.BaseDirectory);
    }
}
