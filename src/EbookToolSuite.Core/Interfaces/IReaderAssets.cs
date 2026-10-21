namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 阅读器前端静态资源（reader.html / reader.css / reader.js）所在的目录。
/// </summary>
/// <remarks>
/// 为什么要单独一个接口而不是直接拼 AppContext.BaseDirectory：
/// 这几个文件在构建时被复制到输出目录，测试工程不会复制它们，
/// 所以单测必须能把它指到别处。见 ReaderService。
/// </remarks>
public interface IReaderAssets
{
    /// <summary>绝对路径，指向含 reader.html 的目录。</summary>
    string Directory { get; }
}