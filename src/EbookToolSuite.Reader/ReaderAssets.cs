using System.IO;
using EbookToolSuite.Core.Interfaces;

namespace EbookToolSuite.Reader;

/// <summary>
/// 阅读器界面文件的位置。构建时 Assets/Reader/** 被复制到输出目录，
/// 所以默认值就是 AppContext.BaseDirectory 下的 Assets/Reader。
/// </summary>
/// <remarks>
/// 这里用 AppContext.BaseDirectory 而不是 IAppPaths.AppRoot：
/// 开发期 AppRoot 是仓库根目录（靠 Tools/manifest.json 往上找），
/// 而界面文件只会在 bin/ 输出目录里，两者不是一回事。
/// </remarks>
public sealed class ReaderAssets : IReaderAssets
{
    /// <summary>给 DI 用的无参构造。</summary>
    public ReaderAssets()
    {
    }

    /// <summary>测试用：把界面文件指到别处。</summary>
    public ReaderAssets(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; } =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Reader");
}