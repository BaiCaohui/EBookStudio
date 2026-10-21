using System.Text;
using System.Text.Json.Nodes;
using EbookToolSuite.Infrastructure.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Infrastructure.Tests.Config;

/// <summary>
/// JsonConfigStore 的验收用例（T1-3b）。规格见设计文档 9.10.4 与 9.10.6。
/// 全部在真实文件系统上跑：这一层的价值就在于真的能落盘、真的能改名。
/// </summary>
public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public JsonConfigStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ets-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "Config.json");
    }

    public void Dispose()
    {
        // 只读用例会把属性改回来，这里再保险一次
        try
        {
            if (File.Exists(_file))
            {
                File.SetAttributes(_file, FileAttributes.Normal);
            }

            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private JsonConfigStore NewStore() => new(_file, NullLogger<JsonConfigStore>.Instance);

    private string Text() => File.ReadAllText(_file, Encoding.UTF8);

    // ---------- 加载 ----------

    [Fact]
    public async Task InitializeAsync_文件不存在_视为空对象并从fallback取值()
    {
        var store = NewStore();

        await store.InitializeAsync();

        Assert.False(File.Exists(_file));
        Assert.Equal("dark", store.GetString("ui.theme", "dark"));
        Assert.True(store.GetBool("ui.showDiagnostics", true));
        Assert.Equal(1.5, store.GetDouble("reader.lineHeight", 1.5));
        Assert.Null(store.Get("ui.theme"));
    }

    [Fact]
    public async Task InitializeAsync_非法JSON_隔离为bad文件并用空配置()
    {
        await File.WriteAllTextAsync(_file, "{ 这不合法 ", new UTF8Encoding(false));

        var store = NewStore();
        await store.InitializeAsync();

        var bad = Path.Combine(_dir, "Config.bad.json");
        Assert.True(File.Exists(bad), "坏文件必须被改名隔离");
        Assert.False(File.Exists(_file), "坏文件应该已经被搬走");
        Assert.Equal("dark", store.GetString("ui.theme", "dark"));
    }

    [Fact]
    public async Task InitializeAsync_根节点不是对象_也隔离()
    {
        await File.WriteAllTextAsync(_file, "[1, 2, 3]", new UTF8Encoding(false));

        var store = NewStore();
        await store.InitializeAsync();

        Assert.True(File.Exists(Path.Combine(_dir, "Config.bad.json")));
        Assert.Equal(0.5, store.GetDouble("a.b", 0.5));
    }

    [Fact]
    public async Task InitializeAsync_容忍注释与尾随逗号()
    {
        await File.WriteAllTextAsync(_file, """
        {
          // 用户手写的注释
          "ui": { "theme": "light" },
        }
        """, new UTF8Encoding(false));

        var store = NewStore();
        await store.InitializeAsync();

        Assert.Equal("light", store.GetString("ui.theme"));
    }
    // ---------- 点分路径读写 ----------

    [Fact]
    public async Task SetAsync_逐级创建中间对象()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.SetAsync("ui.windowStates.main.lastRoute", JsonValue.Create("settings"));

        Assert.Equal("settings", store.GetString("ui.windowStates.main.lastRoute"));
        Assert.Equal(
            "settings",
            store.Get("ui")!["windowStates"]!["main"]!["lastRoute"]!.GetValue<string>());
    }

    [Fact]
    public async Task SetAsync_空路径段_抛参数异常()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SetAsync("ui..theme", JsonValue.Create("light")));
    }

    // ---------- 未知字段保留（本任务的红线） ----------

    [Fact]
    public async Task SaveAsync_程序不认识的字段必须原样保留()
    {
        // 模拟"用户手写了一个本程序还没实现的设置项"，位置是深的、值是中文的
        await File.WriteAllTextAsync(_file, """
        {
          "myTestMarker": "abc",
          "ui": {
            "windowStates": { "main": { "left": 10 } },
            "futureFeature": { "书库": [1, 2, 3] }
          }
        }
        """, new UTF8Encoding(false));

        var store = NewStore();
        await store.InitializeAsync();
        await store.SetAsync("ui.windowStates.main.left", JsonValue.Create(320.0));
        await store.SaveAsync();

        var text = Text();
        Assert.Contains("myTestMarker", text, StringComparison.Ordinal);
        Assert.Contains("abc", text, StringComparison.Ordinal);
        Assert.Contains("futureFeature", text, StringComparison.Ordinal);
        Assert.Contains("书库", text, StringComparison.Ordinal);

        // 改过的那一处是新值，没动的那一处还是原值
        var reread = NewStore();
        await reread.InitializeAsync();
        Assert.Equal(320.0, reread.GetDouble("ui.windowStates.main.left", -1));
        Assert.Equal("abc", reread.GetString("myTestMarker"));
    }

    // ---------- 原子写与文件格式 ----------

    [Fact]
    public async Task SaveAsync_首次保存才创建文件_且不留tmp()
    {
        var store = NewStore();
        await store.InitializeAsync();
        Assert.False(File.Exists(_file));

        await store.SetAsync("version", JsonValue.Create(1));
        await store.SaveAsync();

        Assert.True(File.Exists(_file));
        Assert.False(File.Exists(_file + ".tmp"), "临时文件必须已经被改名走");
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_UTF8无BOM且缩进两个空格()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.SetAsync("ui.theme", JsonValue.Create("light"));
        await store.SaveAsync();

        var bytes = File.ReadAllBytes(_file);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Assert.False(hasBom, "Config.json 不能带 BOM");

        var text = Text();
        Assert.Contains("  " + (char)34 + "ui" + (char)34, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\t", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_中文不被转义成unicode转义()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.SetAsync("library.name", JsonValue.Create("Books库"));
        await store.SaveAsync();

        var text = Text();
        Assert.Contains("Books库", text, StringComparison.Ordinal);
        Assert.DoesNotContain((char)92 + "u", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_两次保存_第二次覆盖第一次()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.SetAsync("ui.theme", JsonValue.Create("light"));
        await store.SaveAsync();

        await store.SetAsync("ui.theme", JsonValue.Create("dark"));
        await store.SaveAsync();

        var reread = NewStore();
        await reread.InitializeAsync();
        Assert.Equal("dark", reread.GetString("ui.theme"));
    }

    [Fact]
    public async Task SaveAsync_目标只读_抛异常且原文件内容不变()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.SetAsync("ui.theme", JsonValue.Create("light"));
        await store.SaveAsync();

        var before = Text();
        File.SetAttributes(_file, FileAttributes.ReadOnly);

        try
        {
            await store.SetAsync("ui.theme", JsonValue.Create("dark"));
            await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync());

            Assert.Equal(before, Text());
            Assert.False(File.Exists(_file + ".tmp"), "失败后不能留下临时文件");
        }
        finally
        {
            File.SetAttributes(_file, FileAttributes.Normal);
        }
    }

    // ---------- 取值 ----------

    [Fact]
    public async Task Get_路径不存在或类型不符_一律回落到fallback()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.SetAsync("ui.theme", JsonValue.Create("light"));

        Assert.Null(store.GetString("ui.nothing"));
        Assert.Equal("x", store.GetString("ui.nothing", "x"));
        Assert.True(store.GetBool("ui.theme", true), "类型不符时用 fallback");
        Assert.Equal(1.25, store.GetDouble("ui.theme", 1.25));
        Assert.Null(store.Get("a.b.c"));
    }
}