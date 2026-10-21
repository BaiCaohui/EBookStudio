<#
.SYNOPSIS
    T2-7：阶段 2（VS2：书库规模化 + 转换队列）端到端验收。

.DESCRIPTION
    对应执行计划 5.1 的 8 步验收脚本与 5.3 的 10 条出口条件。
    分两段：
      【阶段 A】本脚本直接跑 --smoke-vs2-a：造 1000 本可构造样本 -> 批量导入 -> 首屏
                -> 真滚 N 秒（默认 60）-> 搜索。
      【专项段】依次串跑现成的专项冒烟，不重复实现：
                smoke-duplicates（15 本重复只弹一次对话框 + 两级指纹）
                smoke-convert-queue（10 本串行 + 磁盘预检 + 重试分类 + 全部取消）
                smoke-queue-crash（强杀 -> 重启 -> 队列继续）
                smoke-templates（6 个内置模板 + 参数白名单）

    全程约 8~12 分钟（1000 本导入是真导入、滚动是真滚 60 秒、转换是真调 Calibre）。
    只想快速回归阶段 A 时用 -SkipSpecialized，并把 -ScrollSeconds 调小。

    退出码：0 = 全部通过；2 = 有失败；1 = 验收本身跑不起来。

.EXAMPLE
    powershell -File build\smoke-vs2.ps1 -File "TestCorpus\sample-epub-1.epub" `
        -File2 "TestCorpus\sample-epub-2.epub"
#>
[CmdletBinding()]
param(
    # 模板 EPUB（克隆出 1000 本当样本）；也是转换与模板段的输入
    [Parameter(Mandatory = $true)]
    [string]$File,

    # 第二本 EPUB：模板段要两本不同的书（一本 EPUB 输出、一本其它格式）
    [string]$File2,

    # 真滚多少秒。验收口径是 60；快速回归可以传 10
    [int]$ScrollSeconds = 60,

    # 只跑阶段 A，不串跑四个专项冒烟
    [switch]$SkipSpecialized,

    # 连阶段 A 也跳过（单独重跑某个专项段时用）
    [switch]$SkipPhaseA,

    # 只跑某一个专项段（按脚本文件名，例如 smoke-templates.ps1）
    [string]$Only,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$ExePath,

    [string]$DataDir,

    # 验收跑完把数据目录一起删掉。默认保留：里面是 1000 本真实书库，值得拿来人眼看
    [switch]$CleanDataDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

<# 读冒烟报告并把每条判据打出来，返回失败条数。-1 表示报告没生成（这一段整体没跑成）。 #>
function Show-Report([string]$ReportPath) {
    if (-not (Test-Path -LiteralPath $ReportPath)) {
        Write-Host "没有生成报告：$ReportPath" -ForegroundColor Red
        return -1
    }

    $report = Get-Content -LiteralPath $ReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $script:LastCheckCount = $report.Checks.Count
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

    return $failed
}
function Resolve-PathArg([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    $p = if ([IO.Path]::IsPathRooted($value)) { $value } else { Join-Path $repoRoot $value }
    return [IO.Path]::GetFullPath($p)
}

$File = Resolve-PathArg $File
if (-not (Test-Path -LiteralPath $File)) { Write-Host "找不到模板 EPUB：$File" -ForegroundColor Red; exit 1 }

$File2 = Resolve-PathArg $File2
if ($File2 -and -not (Test-Path -LiteralPath $File2)) { Write-Host "找不到第二本 EPUB：$File2" -ForegroundColor Red; exit 1 }

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $exe = Join-Path $repoRoot "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
}
else {
    $exe = Resolve-PathArg $ExePath
}

if (-not (Test-Path -LiteralPath $exe)) { Write-Host "找不到主程序：$exe" -ForegroundColor Red; exit 1 }


# ---------- 数据目录 ----------
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot ("Data-Test\vs2-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}

$DataDir = [IO.Path]::GetFullPath($DataDir)
if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }

Write-Host "主程序  ：$exe"
Write-Host "模板样本：$File"
Write-Host "数据目录：$DataDir"
Write-Host "滚动时长：$ScrollSeconds 秒"
Write-Host ""

$results = [System.Collections.Generic.List[object]]::new()
$totalFailed = 0

# ---------- 阶段 A：1000 本规模 ----------
if ($SkipPhaseA) {
    Write-Host "已跳过阶段 A（-SkipPhaseA）" -ForegroundColor Yellow
    Write-Host ""
}
else {
Write-Host "========== 阶段 A：1000 本规模（导入 / 首屏 / 滚动 / 搜索）==========" -ForegroundColor Cyan

$arguments = '--smoke-vs2-a="' + $File + '" --scroll-seconds=' + $ScrollSeconds + ' --datadir="' + $DataDir + '"'
$phaseAWatch = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait
$phaseAWatch.Stop()

$reportPath = Join-Path $DataDir 'Temp\vs2-smoke.json'
if (-not (Test-Path -LiteralPath $reportPath)) {
    Write-Host "阶段 A 没有生成报告：$reportPath（退出码 $($process.ExitCode)）" -ForegroundColor Red
    exit 1
}

$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json

$aFailed = 0
foreach ($check in $report.Checks) {
    if ($check.Ok) {
        Write-Host ("[OK]   {0,-34} {1}" -f $check.Name, $check.Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("[FAIL] {0,-34} {1}" -f $check.Name, $check.Detail) -ForegroundColor Red
        $aFailed++
    }
}

$results.Add([pscustomobject]@{ Segment = '阶段 A（1000 本规模）'; Checks = $report.Checks.Count; Failed = $aFailed; Seconds = [math]::Round($phaseAWatch.Elapsed.TotalSeconds, 1) })
}
Write-Host ""
$totalFailed += $aFailed
Write-Host ""
# ---------- 专项段：串跑现成的冒烟，不重复实现 ----------
if (-not $SkipSpecialized) {
    $segments = @(
        [pscustomobject]@{ Name = '重复检测（两级指纹 / 只弹一次）'; Script = 'smoke-duplicates.ps1'; Extra = @() }
        [pscustomobject]@{ Name = '转换队列（10 本 / 预检 / 重试 / 取消）'; Script = 'smoke-convert-queue.ps1'; Extra = @('-File', $File) }
        [pscustomobject]@{ Name = '崩溃恢复（强杀 -> 重启继续）'; Script = 'smoke-queue-crash.ps1'; Extra = @('-File', $File) }
        # 模板段直接启动 exe，不经 powershell 包装脚本：嵌套调用包装脚本在受限环境里会
        # 丢输出、退出码也不可靠（实测零输出 + 退出码 -1），而直接启动 exe 只有一层子进程。
        [pscustomobject]@{
            Name = '转换模板（6 个内置 / 白名单）'
            Script = 'smoke-templates.ps1'
            Direct = $true
            Extra = @()
            Report = 'Temp\template-smoke.json'
            AppArgs = '--smoke-templates-epub="{A}" --smoke-templates-b="{B}" --datadir="{DATA}"'
        }
    )

    foreach ($segment in $segments) {
        if ($Only -and $segment.Script -ne $Only) { continue }

        Write-Host ("========== " + $segment.Name + " ==========") -ForegroundColor Cyan

        $scriptPath = if ($segment.Direct) { $null } else { Join-Path $PSScriptRoot $segment.Script }

        if ($segment.Direct -and [string]::IsNullOrWhiteSpace($File2)) {
            Write-Host "跳过：模板段要两本不同的 EPUB，用 -File2 指定第二本" -ForegroundColor Yellow
            $results.Add([pscustomobject]@{ Segment = $segment.Name; Checks = 0; Failed = 0; Seconds = 0 })
            Write-Host ""
            continue
        }

        $callArgs = @()
        $callArgs += $segment.Extra
        $callArgs += @('-Configuration', $Configuration)

        if (-not [string]::IsNullOrWhiteSpace($ExePath)) {
            $callArgs += @('-ExePath', $ExePath)
        }

        <# 用 Start-Process + 输出重定向，而不是 & powershell ... 2>&1：
           两级嵌套（工具壳 -> 本脚本 -> 子脚本）时管道会丢输出、退出码也不可靠，
           实测模板段就是"零输出 + 退出码 -1"。改成把子进程的 stdout 落到文件再读回来，
           输出与退出码都拿得到。每个参数都显式加引号，路径里有空格/括号/逗号也不会被拆。 #>
        if (-not $segment.Direct) {
        $segmentLog = Join-Path $env:TEMP ("vs2-seg-" + [IO.Path]::GetFileNameWithoutExtension($segment.Script) + ".txt")
        Remove-Item -LiteralPath $segmentLog -ErrorAction SilentlyContinue

        $argLine = '-ExecutionPolicy Bypass -File "' + $scriptPath + '"'
        foreach ($one in $callArgs) { $argLine += ' "' + $one + '"' }

        $watch = [Diagnostics.Stopwatch]::StartNew()
        $proc = Start-Process powershell -ArgumentList $argLine -PassThru -Wait -RedirectStandardOutput $segmentLog
        $code = $proc.ExitCode
        $watch.Stop()

        $out = @(Get-Content -LiteralPath $segmentLog -Encoding Default -ErrorAction SilentlyContinue)
        $out | ForEach-Object { Write-Host $_ }

        # 判据数直接数 [OK] 行：各脚本的收尾文案不统一（有的写"全部 N 项通过"，
        # 有的写"恢复冒烟全部通过"），数行数最稳。
        $checkCount = @($out | Where-Object { $_ -match '^\[OK\]' }).Count
        $lastLine = $out | Select-String -Pattern '全部 \d+ 项通过' | Select-Object -Last 1

        if ($lastLine -and $lastLine.ToString() -match '全部 (\d+) 项通过') {
            $parsed = [int]$Matches[1]

            if ($parsed -gt $checkCount) {
                $checkCount = $parsed
            }
        }

        $failedHere = if ($code -eq 0) { 0 } else { 1 }
        }
        else {
            # 直接启动 exe：子进程只有一层，输出与退出码都可靠。
            $segData = Join-Path $repoRoot ("Data-Test\vs2-tpl-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
            New-Item -ItemType Directory -Force -Path $segData | Out-Null

            $q = [char]0x22
            $argLine = $segment.AppArgs.Replace('{A}', $File).Replace('{B}', $File2).Replace('{DATA}', $segData)

            $watch = [Diagnostics.Stopwatch]::StartNew()
            $proc = Start-Process -FilePath $exe -ArgumentList $argLine -PassThru -Wait
            $code = $proc.ExitCode
            $watch.Stop()

            $checkCount = 0
            $failedHere = Show-Report (Join-Path $segData $segment.Report)

            if ($failedHere -lt 0) {
                $failedHere = 1
                $code = 1
            }
            else {
                $checkCount = $script:LastCheckCount
            }
        }
        if ($failedHere -ne 0) { $totalFailed++ }

        if ($failedHere -ne 0) {
            Write-Host ("段失败：退出码 " + $code + "，捕获到 " + @($out).Count + " 行输出") -ForegroundColor Red
            Write-Host ("命令行：powershell -ExecutionPolicy Bypass -File `"" + $scriptPath + "`" " + ($callArgs -join " ")) -ForegroundColor Red
        }

        $results.Add([pscustomobject]@{ Segment = $segment.Name; Checks = $checkCount; Failed = $failedHere; Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) })
        Write-Host ""
    }
}

# ---------- 汇总 ----------
Write-Host "========== 汇总 ==========" -ForegroundColor Cyan
Write-Host ""

foreach ($r in $results) {
    $state = if ($r.Failed -eq 0) { '通过' } else { '失败' }
    Write-Host ("{0,-34} 判据 {1,3} 项   {2}   {3} 秒" -f $r.Segment, $r.Checks, $state, $r.Seconds)
}

Write-Host ""
if ($totalFailed -eq 0) {
    Write-Host ("阶段 2（VS2）验收通过：{0} 段全绿。" -f $results.Count) -ForegroundColor Green
}
else {
    Write-Host ("有 {0} 段失败，见上面的 [FAIL] 行。" -f $totalFailed) -ForegroundColor Red
}

Write-Host ""
Write-Host "只能人眼确认的三项：" -ForegroundColor Yellow
Write-Host "  1. 滚动 60 秒的跟手感（自动判据只看 UI 线程最大停顿 <= 200 ms）"
Write-Host "  2. 1000 本书库快速滚动时封面逐张出现的观感"
Write-Host "  3. 转换出来的 MOBI 用系统阅读器打开是否正常"
Write-Host ""
Write-Host "数据目录保留在：$DataDir" -ForegroundColor Yellow
Write-Host "（1000 本样本在 $DataDir\vs2-source，可以直接用界面打开这本书库看）"

if ($CleanDataDir) {
    try { Remove-Item -LiteralPath $DataDir -Recurse -Force } catch { }
}

if ($totalFailed -gt 0) { exit 2 }
exit 0
