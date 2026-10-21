<#
.SYNOPSIS
    「关掉阅读窗口后书库网格自己刷新进度角标」的回归冒烟（用户要求）。

.DESCRIPTION
    导入一本真实 EPUB，用 ReadCommand 开阅读窗口，翻到可滚动的章节滚到一半，
    立刻关窗（故意不等 1.5 秒防抖），再检查书库网格里的卡片自己跟上了：
      import-ok                       导入成功
      grid-card-present               网格里有这张卡片
      initial-unread                  读之前是"未读、没进度"
      reader-opened-and-rendered      真入口开的窗口，首页渲染出文字
      main-minimize-keeps-reader      主窗口最小化后，阅读窗口还在正常状态（不被一起最小化）
      reader-minimize-keeps-main      阅读窗口最小化时，主窗口不受影响
      restore-keeps-reader-usable     还原后阅读窗口还能取到正文页几何（刷新没坏）
      scrollable-chapter-found        找到一章真的能滚动的（否则拿 0 比 0 是空验）
      close-refreshed-card-progress   关窗后卡片进度自己变了
      card-matches-db                 卡片与库里那条一致
      status-becomes-reading          状态变成"在读"，角标文案不再是"未读"
      flush-wrote-scrolled-position   库里是滚动后的位置（证明关窗前真的先落盘）
      refresh-is-not-full-reload      只重读这一本，没整页重载

    退出码：0 = 全部通过；2 = 有检查项失败；1 = 冒烟本身跑不起来。

.EXAMPLE
    powershell -File build\smoke-reader-refresh.ps1 -File "TestCorpus\sample-epub-1.epub"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$File,

    [string]$DataDir,

    [string]$ExePath,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$KeepDataDir
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $exe = Join-Path $repoRoot "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
}
elseif ([IO.Path]::IsPathRooted($ExePath)) {
    $exe = [IO.Path]::GetFullPath($ExePath)
}
else {
    $exe = [IO.Path]::GetFullPath((Join-Path $repoRoot $ExePath))
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "找不到主程序：$exe" -ForegroundColor Red
    Write-Host "先跑一次 dotnet build -c $Configuration" -ForegroundColor Yellow
    exit 1
}

$source = (Resolve-Path -LiteralPath $File).ProviderPath

$temporary = $false
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot ("Data-Test\reader-refresh-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $temporary = $true
}

$DataDir = [IO.Path]::GetFullPath($DataDir)
if (Test-Path -LiteralPath $DataDir) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}

Write-Host "源文件：$source"
Write-Host "数据目录：$DataDir"
Write-Host ''

# 参数必须整体作为一个带引号的字符串传，否则路径里的空格会被当成分隔符
$arguments = '--smoke-reader-refresh="' + $source + '" --datadir="' + $DataDir + '"'

$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait
$reportPath = Join-Path $DataDir 'Temp\reader-refresh-smoke.json'
if (-not (Test-Path -LiteralPath $reportPath)) {
    Write-Host "没有生成报告：$reportPath（退出码 $($process.ExitCode)）" -ForegroundColor Red
    exit 1
}

$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Host ("耗时 {0} ms" -f $report.DurationMs)
Write-Host ''

$failed = 0
foreach ($check in $report.Checks) {
    if ($check.Ok) {
        Write-Host ("[OK]   {0,-32} {1}" -f $check.Name, $check.Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("[FAIL] {0,-32} {1}" -f $check.Name, $check.Detail) -ForegroundColor Red
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