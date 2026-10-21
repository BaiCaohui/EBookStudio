namespace EbookToolSuite.Engines.Calibre;

/// <summary>
/// 一个 Calibre 命令行选项。TakesValue 从 --help 的 --option=VALUE 写法推断：
/// 带等号的必须给值，不带的必须不给值。
/// </summary>
public sealed record CalibreOption(string Name, bool TakesValue);

/// <summary>
/// 某个 (输入扩展名 -> 输出扩展名) 组合的可用选项集合。
/// 这是 CalibreArgumentBuilder 校验参数的唯一依据。
/// </summary>
public sealed class CalibreOptions
{
    public CalibreOptions(
        string inputExtension, string outputExtension,
        IReadOnlyList<CalibreOption> options, bool isFallback)
    {
        InputExtension = inputExtension;
        OutputExtension = outputExtension;
        Options = options;
        IsFallback = isFallback;
        _byName = new Dictionary<string, CalibreOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            _byName[option.Name] = option;
        }
    }

    private readonly Dictionary<string, CalibreOption> _byName;

    public string InputExtension { get; }

    public string OutputExtension { get; }

    public IReadOnlyList<CalibreOption> Options { get; }

    /// <summary>
    /// true = 探测失败时用的内置兜底清单（只含内置模板用到的选项）。
    /// 这时界面必须提示"无法向 Calibre 核实参数，仅允许内置模板的参数"。
    /// </summary>
    public bool IsFallback { get; }

    public bool IsKnown(string name) => _byName.ContainsKey(name);

    public CalibreOption? Find(string name)
        => _byName.TryGetValue(name, out var option) ? option : null;
}
