<#
.SYNOPSIS
    T2-3 虚拟化网格与搜索筛选的冒烟。

.DESCRIPTION
    验证能用代码验的那部分验收判据：
      1000 本首屏 <= 2.0 秒 / 虚拟化真的只实现可见行 / 封面不是一次全解 /
      连续滚动 2 秒内 UI 线程无 > 200 ms 的卡顿 /
      搜索停止输入后 <= 300 ms 出结果（含 200 ms 防抖）/ 多词是 AND /
      组合筛选（格式=EPUB + 有封面 + 未读）计数正确 /
      三种空状态（库空 / 搜不到 / 筛空）与剔除 chip 都正确

    样本由冒烟自己构造（直接写库 1000 本，不造真 EPUB）：这一层要验的是网格与查询的性能，
    为了验它去导入 1000 本书只会让冒烟又慢又脆。
    滚动跟手感、封面逐张出现的观感仍只能人眼验。
    退出码：0 = 全部通过；2 = 有检查项失败；1 = 冒烟本身跑不起来。

.EXAMPLE
    powershell -File build\smoke-duplicates.ps1
    powershell -File build\smoke-duplicates.ps1 -ExePath publish\EBookStudio.exe
#>
[CmdletBinding()]
param(
    [string]$DataDir,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$KeepDataDir,

    # 传了就验收这个 exe（例如 publish\EBookStudio.exe），不传则用 bin 输出
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $exe = Join-Path $repoRoot "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
}
else {
    $candidate = if ([IO.Path]::IsPathRooted($ExePath)) { $ExePath } else { Join-Path $repoRoot $ExePath }
    $exe = [IO.Path]::GetFullPath($candidate)
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "找不到主程序：$exe" -ForegroundColor Red
    Write-Host "先跑一次 dotnet build -c $Configuration" -ForegroundColor Yellow
    exit 1
}

$temporary = $false
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot ("Data-Test\grid-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $temporary = $true
}

$DataDir = [IO.Path]::GetFullPath($DataDir)
if (Test-Path -LiteralPath $DataDir) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}

Write-Host "数据目录：$DataDir"
Write-Host "正在造样本并扫描（含 1000 本性能判据），请稍等..."

# 参数必须整体作为「一个带引号的字符串」传，否则路径里的空格会被当成参数分隔符
$arguments = '--smoke-grid --datadir="' + $DataDir + '"'

$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait

$reportPath = Join-Path $DataDir 'Temp\grid-smoke.json'
if (-not (Test-Path -LiteralPath $reportPath)) {
    Write-Host "没有生成报告：$reportPath（退出码 $($process.ExitCode)）" -ForegroundColor Red
    exit 1
}

$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Host ''
Write-Host ("耗时 {0} ms" -f $report.DurationMs)
Write-Host ''

$failed = 0
foreach ($check in $report.Checks) {
    if ($check.Ok) {
        Write-Host ("[OK]   {0,-38} {1}" -f $check.Name, $check.Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("[FAIL] {0,-38} {1}" -f $check.Name, $check.Detail) -ForegroundColor Red
        $failed++
    }
}

Write-Host ''
if ($failed -eq 0) {
    Write-Host ("全部 {0} 项通过。" -f $report.Checks.Count) -ForegroundColor Green
}
else {
    Write-Host ("{0} 项失败。" -f $failed) -ForegroundColor Red
}

if ($temporary -and -not $KeepDataDir) {
    try { Remove-Item -LiteralPath $DataDir -Recurse -Force } catch { }
}

if ($failed -gt 0) { exit 2 }
exit 0