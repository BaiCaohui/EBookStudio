using System.Diagnostics;
using System.IO;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;

namespace EbookToolSuite.Infrastructure.SystemIntegration;

/// <summary>
/// 用 ShellExecute 把文件/目录/URL 交给系统默认程序。
/// 这是全应用唯一允许直接 Process.Start 的地方。
/// </summary>
public sealed class ShellLauncher : IShellLauncher
{
    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径为空", nameof(path));
        }

        if (!Directory.Exists(path))
        {
            throw new AppException(
                ErrorCode.FileNotFound,
                "目录不存在：" + path,
                technical: "OpenFolder target missing: " + path);
        }

        Launch(path);
    }

    /// <summary>
    /// 打开文件所在目录并选中它。
    /// explorer 的参数格式很挑：必须是 /select,"完整路径"，**逗号后面不能有空格**，
    /// 路径含空格/中文时必须带引号，否则会变成"打开文档目录"而不是选中文件。
    /// </summary>
    public void RevealInExplorer(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("路径为空", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new AppException(
                ErrorCode.FileNotFound,
                "文件不存在：" + filePath,
                technical: "RevealInExplorer target missing: " + filePath);
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", BuildRevealArguments(filePath))
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            throw new AppException(
                ErrorCode.InternalError,
                "无法打开文件所在目录：" + filePath,
                technical: "explorer /select failed: " + ex.Message,
                inner: ex,
                context: new Dictionary<string, object?> { ["target"] = filePath });
        }
    }

    /// <summary>拼 explorer 的 /select 参数。抽成静态方法是为了能单测这段格式（逗号后不能有空格）。</summary>
    public static string BuildRevealArguments(string filePath) => "/select,\"" + filePath + "\"";
    public void OpenFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径为空", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new AppException(
                ErrorCode.FileNotFound,
                "文件不存在：" + path,
                technical: "OpenFile target missing: " + path);
        }

        Launch(path);
    }

    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL 为空", nameof(url));
        }

        Launch(url);
    }

    private static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            throw new AppException(
                ErrorCode.InternalError,
                "无法用系统默认程序打开：" + target,
                technical: "ShellExecute failed: " + ex.Message,
                inner: ex,
                context: new Dictionary<string, object?> { ["target"] = target });
        }
    }
}