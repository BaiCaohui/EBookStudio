using System.Text.RegularExpressions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;

namespace EbookToolSuite.Infrastructure.Tests.Errors;

/// <summary>
/// T7-2 判据 3：每个错误码都必须有中文文案 + 建议动作（设计文档 v2 第 9 章 / v3 附录 A）。
/// 这个测试按枚举逐个核对，漏一个就红。
/// </summary>
public sealed class ErrorCatalogTests
{
    [Fact]
    public void 每一个错误码都必须在目录里登记()
    {
        var missing = Enum.GetValues<ErrorCode>()
            .Where(code => !ErrorCatalog.Contains(code))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "以下错误码没有中文文案与建议动作：" + string.Join(", ", missing));
    }

    [Fact]
    public void 每条文案都必须非空且有建议动作()
    {
        foreach (var info in ErrorCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(info.Message), info.Code + " 的 Message 为空");
            Assert.False(string.IsNullOrWhiteSpace(info.Action), info.Code + " 的 Action 为空");
            Assert.Matches(new Regex("^ETS-[A-Z]{3,4}-[0-9]{4}$"), info.EtsCode);
        }
    }

    /// <summary>
    /// 设计文档 v2 第 9.1 节的错误码表（T7-2 判据原文引用的那一版）必须全部覆盖。
    /// </summary>
    [Theory]
    [InlineData(ErrorCode.FileLocked)]
    [InlineData(ErrorCode.FileNotFound)]
    [InlineData(ErrorCode.UnsupportedFormat)]
    [InlineData(ErrorCode.DrmDetected)]
    [InlineData(ErrorCode.CorruptFile)]
    [InlineData(ErrorCode.MissingTool)]
    [InlineData(ErrorCode.DiskFull)]
    [InlineData(ErrorCode.EncodingError)]
    [InlineData(ErrorCode.ValidationFailed)]
    [InlineData(ErrorCode.ProcessCrashed)]
    [InlineData(ErrorCode.ProcessTimeout)]
    [InlineData(ErrorCode.ToolVersionMismatch)]
    public void 设计文档第9章的错误码全部覆盖(ErrorCode code)
    {
        Assert.True(ErrorCatalog.Contains(code), code + " 没有登记");
    }

    [Theory]
    [InlineData(ErrorCode.FileLocked, "ETS-GEN-0006")]
    [InlineData(ErrorCode.DiskFull, "ETS-GEN-0005")]
    [InlineData(ErrorCode.AccessDenied, "ETS-GEN-0008")]
    [InlineData(ErrorCode.DrmDetected, "ETS-LIB-0105")]
    [InlineData(ErrorCode.UnsupportedFormat, "ETS-LIB-0102")]
    [InlineData(ErrorCode.MissingTool, "ETS-TOOL-0501")]
    [InlineData(ErrorCode.ToolVersionMismatch, "ETS-TOOL-0502")]
    [InlineData(ErrorCode.ProcessTimeout, "ETS-CONV-0204")]
    [InlineData(ErrorCode.DataDirNotWritable, "ETS-DATA-0605")]
    [InlineData(ErrorCode.ReaderOpenFailed, "ETS-READ-0401")]
    public void 关键错误码映射到正确的域(ErrorCode code, string etsCode)
    {
        Assert.Equal(etsCode, ErrorCatalog.Get(code).EtsCode);
    }

    [Fact]
    public void 未登记的错误码按Unknown兜底而不是抛异常()
    {
        // 枚举强类型下走不到，但目录实现必须防御性兜底。
        var info = ErrorCatalog.Get((ErrorCode)999999);

        Assert.Equal(ErrorCode.Unknown, info.Code);
        Assert.False(string.IsNullOrWhiteSpace(info.Message));
    }
}
