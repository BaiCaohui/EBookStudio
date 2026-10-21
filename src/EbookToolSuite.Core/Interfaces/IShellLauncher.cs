namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 用系统默认程序打开文件/目录/URL。
///
/// 为什么不复用 <see cref="IProcessHost"/>：那个是"跑外部工具并等它退出"，
/// 而 Explorer 永远不会退出，用它会把调用方挂住。这里要的是"交给系统就别管了"，
/// 所以单独一个抽象。设计文档 3.1 的"任何层不得裸 Process.Start"针对的是外部工具，
/// 但为了可测与集中管理，这里仍然收口成接口。
/// </summary>
public interface IShellLauncher
{
    /// <summary>在资源管理器里打开目录。目录不存在时抛 AppException。</summary>
    void OpenFolder(string path);

    /// <summary>
    /// 打开文件所在的目录，并**选中这个文件**（资源管理器的 /select）。
    /// 与 OpenFolder 的区别：那个只是把目录打开，用户还得自己在里面找。
    /// </summary>
    void RevealInExplorer(string filePath);
    /// <summary>用系统默认程序打开文件。</summary>
    void OpenFile(string path);

    /// <summary>用默认浏览器打开 URL。</summary>
    void OpenUrl(string url);
}