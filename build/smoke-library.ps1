<#
.SYNOPSIS
    T1-3 书库封面网格的冒烟。

.DESCRIPTION
    验证能用代码验的那部分验收判据：
      空库显示空状态 / 50 本书全部列出来 / 封面解码成功且已 Freeze /
      双击绑定接在打开详情的命令上 / 详情显示标题与元数据 /
      阅读与转换按钮在阶段 1 是禁用且带说明 / 关闭详情后滚动位置不丢 /
      删掉 Covers 目录后封面回落占位图且不崩

    50 本样本由冒烟自己构造（占位文件 + 真 JPEG 缩略图），不依赖TestCorpus目录。
    滚动是否跟手、封面逐张出现的观感只能人眼验，报告里会列出来提醒。

    退出码：0 = 全部通过；2 = 有检查项失败；1 = 冒烟本身跑不起来。

.EXAMPLE
    powershell -File build\smoke-library.ps1
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
    # 直接验收发布产物（publish\EBookStudio.exe）。发布产物才是最终形态，
    # 只在 Debug 输出上跑冒烟会漏掉"产物比源码旧"这一类问题。
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
    $DataDir = Join-Path $repoRoot ("Data-Test\library-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $temporary = $true
}

$DataDir = [IO.Path]::GetFullPath($DataDir)
if (Test-Path -LiteralPath $DataDir) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}

Write-Host "数据目录：$DataDir"

# 参数必须整体作为「一个带引号的字符串」传，否则路径里的空格会被当成参数分隔符
$arguments = '--smoke-library --datadir="' + $DataDir + '"'

$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait

$reportPath = Join-Path $DataDir 'Temp\library-smoke.json'
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
        Write-Host ("[OK]   {0,-40} {1}" -f $check.Name, $check.Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("[FAIL] {0,-40} {1}" -f $check.Name, $check.Detail) -ForegroundColor Red
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