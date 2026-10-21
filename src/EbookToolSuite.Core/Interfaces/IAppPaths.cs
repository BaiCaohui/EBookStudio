namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 全部文件系统路径的唯一来源。全工程禁止硬编码路径字符串，
/// 也禁止在别处再写 Path.Combine(AppContext.BaseDirectory, ...)。
/// 目录布局见设计文档 4.1。
/// </summary>
public interface IAppPaths
{
    // ---------- 程序目录（只读，绿色便携） ----------

    /// <summary>程序根目录，即 AppContext.BaseDirectory。可整体拷贝到 U 盘。</summary>
    string AppRoot { get; }

    /// <summary>外部工具根目录（Tools/）。</summary>
    string ToolsRoot { get; }

    /// <summary>工具清单文件（Tools/manifest.json）。</summary>
    string ToolsManifestFile { get; }

    // ---------- 数据目录（唯一允许写入的区域） ----------

    /// <summary>数据根目录。默认是 AppRoot/Data，可被外置。</summary>
    string DataRoot { get; }

    /// <summary>用户设置（Data/Config.json）。</summary>
    string ConfigFile { get; }

    /// <summary>SQLite 主库（Data/Library.db，另有 -wal 与 -shm 伴生文件）。</summary>
    string DbFile { get; }

    /// <summary>书籍文件正式区（Data/Library/）。导入时复制到这里，原件不动。</summary>
    string LibraryDir { get; }

    /// <summary>Calibre 配置隔离目录，作为 CALIBRE_CONFIG_DIRECTORY 注入。</summary>
    string CalibreConfigDir { get; }

    /// <summary>WebView2 userDataFolder（Data/WebView2Data/）。</summary>
    string WebView2DataDir { get; }

    /// <summary>编辑工作区根（Data/Workspaces/{sessionId}/）。</summary>
    string WorkspacesDir { get; }

    /// <summary>临时文件（Data/Temp/）。启动时清理超过 24 小时的 .part/.tmp。</summary>
    string TempDir { get; }

    /// <summary>日志（Data/Logs/app-yyyyMMdd.log，UTF-8 无 BOM，保留 15 天）。</summary>
    string LogsDir { get; }

    // ---------- 缓存 ----------

    /// <summary>缓存根（Data/Cache/）。是唯一允许整体删除的目录。</summary>
    string CacheDir { get; }

    /// <summary>封面缓存（Data/Cache/Covers/{bookId}.jpg 与 {bookId}_t.jpg）。</summary>
    string CoversCacheDir { get; }

    /// <summary>MOBI/AZW3 转出的临时 EPUB、TXT 生成物（Data/Cache/Reader/{hash}/）。</summary>
    string ReaderCacheDir { get; }

    /// <summary>Calibre 缓存，作为 CALIBRE_CACHE_DIRECTORY 注入。</summary>
    string CalibreCacheDir { get; }

    /// <summary>
    /// PDF 页位图缓存的落点（Data/Cache/Pages/）。T6-2B 的结论是**页位图只在内存**
    /// （默认 20 页 LRU，判据⑤⑥），所以这个目录既不预建、也不会有文件；
    /// 留着它是给将来真要做磁盘页缓存时的落点（设计文档 11.4）。
    /// </summary>
    string PagesCacheDir { get; }

    /// <summary>
    /// 导出产物（Data/Exports/）。T6-5 的书签与笔记 Markdown 落在这里：
    /// 用户点「导出」得到的文件要能被"用系统默认程序打开"，放 Temp 会被启动清理误删。
    /// </summary>
    string ExportsDir { get; }

    // ---------- 备份 ----------

    /// <summary>备份根（Data/Backup/）。</summary>
    string BackupDir { get; }

    /// <summary>单文件备份（Data/Backup/Files/{bookId}/{yyyyMMddHHmmss}_{name}.bak）。</summary>
    string BackupFilesDir { get; }

    /// <summary>还原点（Data/Backup/RestorePoints/{pointId}/）。</summary>
    string RestorePointsDir { get; }

    /// <summary>数据库定期备份（Data/Backup/Database/library-{yyyyMMdd}.db）。</summary>
    string BackupDatabaseDir { get; }

    // ---------- 数据目录指针（T7-4：迁移向导与首次运行向导用） ----------

    /// <summary>用户本地根目录（%LOCALAPPDATA%\EbookToolSuite），指针文件的存放处。</summary>
    string UserLocalRoot { get; }

    /// <summary>
    /// 指针文件绝对路径（datadir.txt）。它在数据目录之外，是定位数据目录的锚点：
    /// Config.json 本身就在数据目录里，数据目录外置后无法自举定位（设计文档 §4.4）。
    /// </summary>
    string PointerFile { get; }

    /// <summary>写指针文件（原子写）。数据目录在程序目录下时通常不需要调用它。</summary>
    void WritePointerFile(string dataRoot);

    /// <summary>删除指针文件（等价于"回到程序目录下的 Data/"）。</summary>
    void DeletePointerFile();

    // ---------- 生命周期 ----------

    /// <summary>
    /// 创建全部子目录。必须在任何写操作之前调用一次。
    /// 幂等：目录已存在时不做任何事。
    /// </summary>
    void EnsureCreated();
}