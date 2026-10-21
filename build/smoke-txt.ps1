<#
.SYNOPSIS
    T4-5 TXT 端到端冒烟。

.DESCRIPTION
    导入一个 .txt，开阅读器，验证 9 项：
      A 导入成功（TXT 也能入库）
      B 打开缓存（章节数 / 编码 / 是否硬切 / 耗时）
      C 编码识别结果在支持列表里
      D 第一章物化出可读中文（含汉字、有 <p>）
      E 目录由切出来的章节构成
      F 界面出现编码选择器，当前值 = 识别结果（判据 2）
      G 手选另一个编码 -> 立即重排（内容确实变了）
      H 选回正确编码 -> 内容恢复
      I 选择落盘到 Config.json

    退出码：0 = 全部通过；2 = 有检查项失败；1 = 冒烟本身跑不起来。

.EXAMPLE
    powershell -File build\smoke-txt.ps1 -File "build\samples\txt-smoke-sample-gbk.txt"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$File,

    [string]$DataDir,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # 直接指定被测的可执行文件（发布产物验收用；不传就用 src 下的构建产物）
    [string]$ExePath,

    # 看门狗：程序里任何模态弹窗都会让 Start-Process -Wait 永远等下去（T3-UI 踩过）
    [int]$TimeoutSeconds = 300,

    [switch]$KeepDataDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $exe = Join-Path $repoRoot "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
}
else {
    $exe = (Resolve-Path -LiteralPath $ExePath).ProviderPath
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "找不到主程序：$exe" -ForegroundColor Red
    Write-Host "先跑一次 dotnet build -c $Configuration" -ForegroundColor Yellow
    exit 1
}

$source = (Resolve-Path -LiteralPath $File).ProviderPath

$temporary = $false
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot ("Data-Test\txt-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
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
$arguments = '--smoke-txt="' + $source + '" --datadir="' + $DataDir + '"'

$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru

if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    Write-Host ("超时 {0} 秒，强杀进程（多半是弹了模态框）" -f $TimeoutSeconds) -ForegroundColor Red
    try { $process.Kill($true) } catch { }
    exit 1
}

$reportPath = Join-Path $DataDir 'Temp\txt-smoke.json'
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
        Write-Host ("[OK]   {0,-30} {1}" -f $check.Name, $check.Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("[FAIL] {0,-30} {1}" -f $check.Name, $check.Detail) -ForegroundColor Red
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
