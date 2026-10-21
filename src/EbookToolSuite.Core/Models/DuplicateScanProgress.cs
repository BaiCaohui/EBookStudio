namespace EbookToolSuite.Core.Models;

/// <summary>重复检测的进度回调（主要在慢的哈希复核阶段用）。</summary>
public sealed record DuplicateScanProgress(string Phase, int Done, int Total)
{
    public double Percent => Total <= 0 ? 0 : (double)Done * 100 / Total;
}

