<#
.SYNOPSIS
    T2-6 文件监控与缺失检测的冒烟。

.DESCRIPTION
    按执行计划的四条判据走一遍（样本是一本真 EPUB）：
      1. 手工删掉文件 -> 刷新 -> 这本书显示"缺失"、库 missing=1、工具栏出现摘要，且不崩
      2. 外部改了文件 -> 刷新 -> 显示"已修改"，且可以"接受改动"
      3. 文件改回来 -> 刷新 -> 恢复正常
      4. 1000 本真文件的库，全库扫描 <= 3 秒

    退出码：0 = 全部通过；2 = 有检查项失败；1 = 冒烟本身跑不起来。

.EXAMPLE
    powershell -File build\smoke-scan.ps1 -File "TestCorpus\sample-epub-1.epub"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$File,

    [string]$DataDir,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$KeepDataDir,

    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not [IO.Path]::IsPathRooted($File)) { $File = Join-Path $repoRoot $File }
$File = [IO.Path]::GetFullPath($File)

if (-not (Test-Path -LiteralPath $File)) {
    Write-Host "找不到样本：$File" -ForegroundColor Red
    exit 1
}
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $exe = Join-Path $repoRoot "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
}
else {
    $candidate = if ([IO.Path]::IsPathRooted($ExePath)) { $ExePath } else { Join-Path $repoRoot $ExePath }
    $exe = [IO.Path]::GetFullPath($candidate)
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "找不到主程序：$exe" -ForegroundColor Red
    exit 1
}

$temporary = $false
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot ("Data-Test\scan-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $temporary = $true
}

$DataDir = [IO.Path]::GetFullPath($DataDir)
if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }

Write-Host "数据目录：$DataDir"

$arguments = '--smoke-scan="' + $File + '" --datadir="' + $DataDir + '"'
$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait

$reportPath = Join-Path $DataDir 'Temp\scan-smoke.json'
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
        Write-Host ("[OK]   {0,-34} {1}" -f $check.Name, $check.Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("[FAIL] {0,-34} {1}" -f $check.Name, $check.Detail) -ForegroundColor Red
        $failed++
    }
}

Write-Host ''
if ($failed -eq 0) { Write-Host ("全部 {0} 项通过。" -f $report.Checks.Count) -ForegroundColor Green }
else { Write-Host ("{0} 项失败。" -f $failed) -ForegroundColor Red }

if ($temporary -and -not $KeepDataDir) {
    try { Remove-Item -LiteralPath $DataDir -Recurse -Force } catch { }
}

if ($failed -gt 0) { exit 2 }
exit 0