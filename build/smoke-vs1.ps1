<#
.SYNOPSIS
    T1-7 VS1 端到端验收：按执行计划 4.1 的 11 步脚本走一遍。

.DESCRIPTION
    "关闭程序、重新启动"这一步必须跨越真实的进程边界，所以拆成两个阶段两个进程：

      阶段 A（步骤 1-5）：全新数据目录 -> 冷启动计时 -> 导入 EPUB ->
                        网格出现封面 -> 开阅读器读到第 2 章中间 -> 关窗退出
      阶段 B（步骤 6-11）：暖启动计时 -> 打开同一本书校验回到原位 ->
                         转 MOBI 并校验产物 -> 再转一次中途取消

    两个阶段都退出之后，本脚本再检查：
      - Data\Cache\calibre 下有没有 conv-* 残留目录（步骤 11）
      - Logs\ 里有没有 [ERR] / [FTL]（出口条件"全程无未处理异常"）

    只列不自动验的那几项（MOBI 用系统阅读器打开、滚动是否跟手、排版观感）
    会在最后单独提示，属于人眼项。

    退出码：0 = 全部通过；2 = 有检查项失败；1 = 冒烟本身跑不起来。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\smoke-vs1.ps1 -File "TestCorpus\sample-epub-1.epub"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$File,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # 指定就用这个 exe（拿来验收 publish\ 里的发布产物）；不指定就按 $Configuration 找 bin 输出。
    [string]$ExePath,

    [string]$DataDir,

    [switch]$KeepDataDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $exe = Join-Path $repoRoot "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
}
else {
    $exe = [IO.Path]::GetFullPath($ExePath)
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "找不到主程序：$exe" -ForegroundColor Red
    Write-Host "先跑一次 dotnet build -c $Configuration，或用 -ExePath 指定一个已存在的发布产物" -ForegroundColor Yellow
    exit 1
}

$source = [IO.Path]::GetFullPath($File)
if (-not (Test-Path -LiteralPath $source)) {
    Write-Host "找不到样本文件：$source" -ForegroundColor Red
    exit 1
}

$temporary = $false
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot ("Data-Test\vs1-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $temporary = $true
}

$DataDir = [IO.Path]::GetFullPath($DataDir)

# 步骤 1：全新状态。这里必须真的删掉，否则"冷启动"和"读到哪了"都不可信。
if (Test-Path -LiteralPath $DataDir) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}

Write-Host ''
Write-Host '===== T1-7 VS1 端到端验收 =====' -ForegroundColor Cyan
Write-Host "样本文件：$source"
Write-Host "数据目录：$DataDir（全新）"
Write-Host ''

$failures = @()

function Show-Report {
    param([string]$Label, $Report)

    Write-Host "--- $Label（用时 $($Report.DurationMs) ms）---" -ForegroundColor Cyan

    foreach ($check in $Report.Checks) {
        if ($check.Ok) {
            Write-Host ("[OK]   {0,-28} {1}" -f $check.Name, $check.Detail) -ForegroundColor Green
        }
        else {
            Write-Host ("[FAIL] {0,-28} {1}" -f $check.Name, $check.Detail) -ForegroundColor Red
            $script:failures += "$Label/$($check.Name)"
        }
    }
}

function Invoke-Phase {
    param([string]$Switch, [string]$OptionName, [string]$ReportName)

    # 参数必须整体作为一个带引号的字符串传，否则路径里的空格会被当分隔符
    $arguments = $Switch + '="' + $source + '" --datadir="' + $DataDir + '"'

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait
    $watch.Stop()

    $reportPath = Join-Path $DataDir "Temp\$ReportName"
    if (-not (Test-Path -LiteralPath $reportPath)) {
        Write-Host "阶段 $Switch 没有生成报告：$reportPath（退出码 $($process.ExitCode)）" -ForegroundColor Red
        exit 1
    }

    $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json

    Write-Host ("进程 {0}：退出码={1} 进程总耗时={2} ms" -f $Switch, $process.ExitCode, $watch.ElapsedMilliseconds)

    return $report
}
# ---------- 阶段 A（步骤 1-5） ----------

$reportA = Invoke-Phase -Switch '--smoke-vs1a' -ReportName 'vs1-a.json'
Show-Report -Label '阶段 A：冷启动 + 导入 + 读到第 2 章中间' -Report $reportA

Write-Host ''

# ---------- 阶段 B（步骤 6-11） ----------

$reportB = Invoke-Phase -Switch '--smoke-vs1b' -ReportName 'vs1-b.json'
Show-Report -Label '阶段 B：暖启动 + 回到原位 + 转 MOBI + 中途取消' -Report $reportB

Write-Host ''
Write-Host '--- 两个阶段都退出之后的检查 ---' -ForegroundColor Cyan

# 步骤 11：Calibre 的运行目录必须已被清理
$calibreTemp = Join-Path $DataDir 'Temp\calibre'
$leftovers = @()

if (Test-Path -LiteralPath $calibreTemp) {
    $leftovers = @(Get-ChildItem -LiteralPath $calibreTemp -Directory -Filter 'conv-*' -ErrorAction SilentlyContinue)
}

if ($leftovers.Count -eq 0) {
    Write-Host ("[OK]   {0,-28} {1}" -f 'exit-tmp-cleaned', 'Temp\calibre 下 conv-* 残留目录=0') -ForegroundColor Green
}
else {
    $names = ($leftovers | ForEach-Object { $_.Name }) -join ', '
    Write-Host ("[FAIL] {0,-28} {1}" -f 'exit-tmp-cleaned', "残留 $($leftovers.Count) 个：$names") -ForegroundColor Red
    $script:failures += 'exit-tmp-cleaned'
}

# 出口条件"全过程无未处理异常"：Logs 里不该有 [ERR] / [FTL]
$logsDir = Join-Path $DataDir 'Logs'
$bad = @()

if (Test-Path -LiteralPath $logsDir) {
    foreach ($log in Get-ChildItem -LiteralPath $logsDir -Filter 'app-*.log' -File) {
        $bad += @(Select-String -LiteralPath $log.FullName -Pattern '\[ERR\]|\[FTL\]' -Encoding UTF8)
    }
}

if ($bad.Count -eq 0) {
    Write-Host ("[OK]   {0,-28} {1}" -f 'exit-no-error-log', 'Logs 里没有 [ERR] / [FTL]') -ForegroundColor Green
}
else {
    $first = ($bad | Select-Object -First 3 | ForEach-Object { $_.Line.Trim() }) -join ' | '
    Write-Host ("[FAIL] {0,-28} {1}" -f 'exit-no-error-log', "发现 $($bad.Count) 条错误日志：$first") -ForegroundColor Red
    $script:failures += 'exit-no-error-log'
}

Write-Host ''
Write-Host ('=' * 46)

if ($failures.Count -eq 0) {
    Write-Host '自动化部分全部通过。' -ForegroundColor Green
}
else {
    Write-Host "失败 $($failures.Count) 项：$($failures -join '; ')" -ForegroundColor Red
}

Write-Host ''
Write-Host '数据目录保留在原处（里面就是验收产物与日志，可供人工核对）：' -ForegroundColor DarkGray
Write-Host "  $DataDir" -ForegroundColor Cyan
Write-Host ''
Write-Host '以下三项只能人眼确认，脚本不代劳：' -ForegroundColor Yellow
Write-Host '  1. 打开上面数据目录里的 Library\<书>\*.mobi，双击用系统阅读器打开，确认能读（脚本只验了文件头是 BOOKMOBI）'
Write-Host '  2. 书库网格滚动是否跟手、封面逐张出现的观感'
Write-Host '  3. 阅读器排版观感（字体、行距、图片位置）'

if ($failures.Count -gt 0) { exit 2 }
exit 0
