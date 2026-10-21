# T7-5 一键快速回归（计划里的「10 分钟版」）
#
# 6 步主干：导入 1 本 EPUB -> 阅读 -> 转 MOBI -> 改元数据 -> 保存 -> EpubCheck。
# 这 6 步能过，说明主干没坏。完整 40 样本回归用 build\regression-t75.ps1 -Scope full。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\quick-regression.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\quick-regression.ps1 -KeepDataDir
#
# 退出码：0 = 全过；2 = 有检查失败；1 = 跑不起来。

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$ExePath,

    [switch]$SkipBuild,

    [switch]$KeepDataDir
)

$ErrorActionPreference = 'Stop'

$script = Join-Path $PSScriptRoot 'regression-t75.ps1'
if (-not (Test-Path -LiteralPath $script)) {
    Write-Host "缺少 $script" -ForegroundColor Red
    exit 1
}

$argLine = '-NoProfile -ExecutionPolicy Bypass -File "' + $script + '" -Scope quick -Configuration ' + $Configuration
if (-not [string]::IsNullOrWhiteSpace($ExePath)) { $argLine = $argLine + ' -ExePath "' + $ExePath + '"' }
if ($SkipBuild) { $argLine = $argLine + ' -SkipBuild' }
if ($KeepDataDir) { $argLine = $argLine + ' -KeepDataDir' }

Write-Host '===== T7-5 一键快速回归（导入 -> 阅读 -> 转 MOBI -> 改元数据 -> 保存 -> EpubCheck） =====' -ForegroundColor Cyan

$process = Start-Process -FilePath 'powershell' -ArgumentList $argLine -PassThru -NoNewWindow
$process.WaitForExit()
$process.WaitForExit()
exit [int]$process.ExitCode
