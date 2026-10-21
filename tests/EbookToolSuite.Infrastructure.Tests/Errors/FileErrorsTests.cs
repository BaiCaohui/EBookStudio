using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;

namespace EbookToolSuite.Infrastructure.Tests.Errors;

/// <summary>
/// T7-2 第 1/2/3/13 项：系统异常的 Win32 分类与中文翻译。
/// </summary>
public sealed class FileErrorsTests
{
    [Theory]
    [InlineData(32, ErrorCode.FileLocked)]    // ERROR_SHARING_VIOLATION
    [InlineData(33, ErrorCode.FileLocked)]    // ERROR_LOCK_VIOLATION
    [InlineData(39, ErrorCode.DiskFull)]      // ERROR_HANDLE_DISK_FULL
    [InlineData(112, ErrorCode.DiskFull)]     // ERROR_DISK_FULL
    [InlineData(21, ErrorCode.DataDirNotWritable)] // ERROR_NOT_READY（盘被拔掉）
    [InlineData(5, ErrorCode.AccessDenied)]   // ERROR_ACCESS_DENIED
    [InlineData(19, ErrorCode.AccessDenied)]  // ERROR_WRITE_PROTECT
    [InlineData(3, ErrorCode.FileNotFound)]   // ERROR_PATH_NOT_FOUND
    [InlineData(0, ErrorCode.InternalError)]
    public void Win32错误码分类正确(int win32, ErrorCode expected)
    {
        Assert.Equal(expected, FileErrors.ByWin32(win32).Code);
    }

    [Fact]
    public void 共享冲突翻译成被占用而且不含英文()
    {
        var io = new IOException("The process cannot access the file", unchecked((int)0x80070020));
        var translated = FileErrors.Translate(io, "保存《测试书》");

        Assert.Equal(ErrorCode.FileLocked, translated.Code);
        Assert.Contains("占用", translated.UserMessage);
        Assert.Contains("保存《测试书》", translated.UserMessage);
        Assert.DoesNotContain("IOException", translated.UserMessage);
        Assert.DoesNotContain("cannot access", translated.UserMessage);
    }

    [Fact]
    public void 磁盘满翻译成空间不足且带建议()
    {
        var io = new IOException("There is not enough space on the disk", unchecked((int)0x80070070));
        var translated = FileErrors.Translate(io, "导入");

        Assert.Equal(ErrorCode.DiskFull, translated.Code);
        Assert.Contains("空间", translated.UserMessage);
    }

    [Fact]
    public void 盘符消失翻译成数据目录不可用()
    {
        var io = new IOException("The device is not ready", unchecked((int)0x80070015));
        var translated = FileErrors.Translate(io, "读取书库");

        Assert.Equal(ErrorCode.DataDirNotWritable, translated.Code);
        Assert.Contains("磁盘", translated.UserMessage);
    }

    [Fact]
    public void 已经是AppException时原样返回不重复包装()
    {
        var original = new AppException(ErrorCode.CorruptFile, "文件坏了");

        Assert.Same(original, FileErrors.Translate(original, "随便"));
    }

    [Fact]
    public void SafeMessage对AppException取UserMessage而不是technical()
    {
        var ex = new AppException(
            ErrorCode.FileLocked,
            "文件被占用，请关掉占用程序后重试。",
            technical: "IOException: sharing violation");

        Assert.Equal("文件被占用，请关掉占用程序后重试。", ex.SafeMessage());
    }

    [Fact]
    public void SafeMessage对系统异常给中文而不是英文原文()
    {
        var ex = new IOException("Access is denied", unchecked((int)0x80070005));
        var text = ex.SafeMessage();

        Assert.Contains("权限", text);
        Assert.DoesNotContain("Access is denied", text);
    }
}
