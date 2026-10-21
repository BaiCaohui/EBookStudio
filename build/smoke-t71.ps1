<#
.SYNOPSIS
    T7-1 性能达标验收：执行计划 10.1 的测量清单逐项实测。

.DESCRIPTION
    冷启动与暖启动必须跨真实进程边界量，所以分两段：

      1. 启动耗时：用 --smoke-t71-boot=<json> 跑 21 个进程。
         全新数据目录第一次 = 冷启动（1 次）；同一目录后续 = 暖启动（20 次，取 P50/P90）。
      2. 进程内性能：用 --smoke-t71=<样本目录> 跑一个进程，量
         1000 本首屏 / 搜索 / 60 秒滚动 / 大 EPUB 首屏 / 20 次取消（响应、残留、句柄增长）/
         连续读 10 本内存。

    样本由脚本准备：一本 >= 5 MB 的真实 EPUB（仓库里没有正好 5 MB 的，取最接近的、
    比 5 MB 大的那本，属于偏严格），以及 15 本内容互不相同的真实小书（冒烟从中导入 10 本）。

    验收用发布产物（默认 publish\EBookStudio.exe），不用 Debug 数字充数。
    退出码：0 = 全部达标；2 = 有项目未达标；1 = 脚本本身跑不起来。
#>
[CmdletBinding()]
param(
    [string]$ExePath,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$DataDir,

    [string]$FixturesDir,

    [switch]$KeepDataDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $publish = Join-Path $repoRoot 'publish\EBookStudio.exe'
    if (Test-Path -LiteralPath $publish) {
        $exe = $publish
    }
    else {
        $exe = Join-Path $repoRoot "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
    }
}
else {
    $exe = [IO.Path]::GetFullPath($ExePath)
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "找不到主程序：$exe" -ForegroundColor Red
    Write-Host "先跑 build\publish.ps1，或用 -ExePath 指定一个已存在的发布产物" -ForegroundColor Yellow
    exit 1
}

$sourceDir = Join-Path $repoRoot 'TestCorpus'
if (-not (Test-Path -LiteralPath $sourceDir)) {
    Write-Host "找不到测试样本目录：$sourceDir" -ForegroundColor Red
    exit 1
}

$epubs = @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.epub' -File | Sort-Object Length)
if ($epubs.Count -lt 11) {
    Write-Host "测试样本里 EPUB 不足 11 本（实际 $($epubs.Count)）" -ForegroundColor Red
    exit 1
}

$large = @($epubs | Where-Object { $_.Length -ge 5MB } | Select-Object -First 1)
if ($large.Count -eq 0) {
    Write-Host '测试样本里没有 >= 5 MB 的 EPUB，无法按 T7-1 的大书口径实测' -ForegroundColor Red
    exit 1
}

if ([string]::IsNullOrWhiteSpace($FixturesDir)) {
    $FixturesDir = Join-Path $repoRoot 'Data-Test\t71-fixtures'
}
$FixturesDir = [IO.Path]::GetFullPath($FixturesDir)

if (Test-Path -LiteralPath $FixturesDir) {
    Remove-Item -LiteralPath $FixturesDir -Recurse -Force
}
New-Item -ItemType Directory -Path $FixturesDir -Force | Out-Null

$largeTarget = Join-Path $FixturesDir 'epub-large.epub'
Copy-Item -LiteralPath $large[0].FullName -Destination $largeTarget -Force

# 小书必须内容互不相同，否则导入会被去重挡掉。按 SHA256 去重后再拿 15 本。
$smallTargets = @()
$seenHashes = @{}
$index = 0

foreach ($file in $epubs) {
    if ($file.FullName -eq $large[0].FullName) { continue }
    if ($smallTargets.Count -ge 15) { break }

    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if ($seenHashes.ContainsKey($hash)) { continue }
    $seenHashes[$hash] = $true

    $index++
    $target = Join-Path $FixturesDir ('book-{0:D2}.epub' -f $index)
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    $smallTargets += $target
}

if ($smallTargets.Count -lt 10) {
    Write-Host "能准备的小书不足 10 本（实际 $($smallTargets.Count)）" -ForegroundColor Red
    exit 1
}

$temporary = $false
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot ('Data-Test\t71-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $temporary = $true
}
$DataDir = [IO.Path]::GetFullPath($DataDir)

# 冷启动必须是全新目录：旧库会让建库/迁移的时间被跳过。
if (Test-Path -LiteralPath $DataDir) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}

Write-Host ''
Write-Host '===== T7-1 性能达标验收 =====' -ForegroundColor Cyan
Write-Host "程序：$exe"
Write-Host "数据目录：$DataDir（全新）"
Write-Host "样本目录：$FixturesDir"
Write-Host "大书：$($large[0].Name)（$([math]::Round($large[0].Length / 1MB, 2)) MB）"
Write-Host "小书：$($smallTargets.Count) 本"
Write-Host ''

$failures = @()

function Get-Percentile {
    param([long[]]$Values, [double]$P)

    if ($Values.Count -eq 0) { return 0 }

    $sorted = @($Values | Sort-Object)
    $rank = ($sorted.Count - 1) * $P
    $lo = [int][math]::Floor($rank)
    $hi = [int][math]::Ceiling($rank)

    if ($lo -eq $hi) { return [long]$sorted[$lo] }

    return [long][math]::Round($sorted[$lo] + ($sorted[$hi] - $sorted[$lo]) * ($rank - $lo))
}

function Invoke-BootProbe {
    param([string]$JsonPath, [string]$Label)

    if (Test-Path -LiteralPath $JsonPath) {
        Remove-Item -LiteralPath $JsonPath -Force
    }

    $arguments = '--smoke-t71-boot="' + $JsonPath + '" --datadir="' + $DataDir + '"'
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait

    if ($process.ExitCode -ne 0) {
        throw "$Label 启动探针退出码 $($process.ExitCode)"
    }

    if (-not (Test-Path -LiteralPath $JsonPath)) {
        throw "$Label 启动探针没有写报告：$JsonPath"
    }

    $payload = Get-Content -LiteralPath $JsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $payload.loaded -or $payload.startupMs -le 0) {
        throw "$Label 启动探针没有等到主窗口 Loaded（startupMs=$($payload.startupMs)）"
    }

    return [long]$payload.startupMs
}

# ---------- 1. 冷启动（全新数据目录） ----------

Write-Host '--- 冷启动（全新数据目录）---' -ForegroundColor Cyan

$coldJson = Join-Path $DataDir 'Temp\t71-boot-cold.json'
$coldMs = Invoke-BootProbe -JsonPath $coldJson -Label '冷启动'
Write-Host ("冷启动 = {0} ms" -f $coldMs)

# ---------- 2. 暖启动（同一数据目录，20 次） ----------

Write-Host ''
Write-Host '--- 暖启动（同一数据目录，20 次）---' -ForegroundColor Cyan

$warmValues = @()
for ($i = 1; $i -le 20; $i++) {
    $json = Join-Path $DataDir ("Temp\t71-boot-warm-{0:D2}.json" -f $i)
    $ms = Invoke-BootProbe -JsonPath $json -Label ("暖启动第 {0} 次" -f $i)
    $warmValues += $ms
    Write-Host ("  第 {0,2} 次 = {1} ms" -f $i, $ms)
}

$p50 = Get-Percentile -Values $warmValues -P 0.50
$p90 = Get-Percentile -Values $warmValues -P 0.90

Write-Host ''
Write-Host ("暖启动：P50 = {0} ms（判据 <= 2000），P90 = {1} ms（判据 <= 2500）" -f $p50, $p90)

# ---------- 3. 进程内性能 ----------

Write-Host ''
Write-Host '--- 进程内性能（1000 本 / 搜索 / 60 秒滚动 / 大书 / 取消 / 内存）---' -ForegroundColor Cyan

$reportPath = Join-Path $DataDir 'Temp\t71-smoke.json'
$arguments = '--smoke-t71="' + $FixturesDir + '" --datadir="' + $DataDir + '"'
$watch = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru

if (-not $process.WaitForExit(1800000)) {
    $process.Kill()
    Write-Host '进程内性能冒烟超过 30 分钟，已强杀' -ForegroundColor Red
    exit 1
}

$watch.Stop()
$exitCode = $process.ExitCode
Write-Host ("进程退出码={0} 总耗时={1} 秒" -f $exitCode, [math]::Round($watch.Elapsed.TotalSeconds, 1))

if (-not (Test-Path -LiteralPath $reportPath)) {
    Write-Host "没有生成性能报告：$reportPath" -ForegroundColor Red
    exit 1
}

$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json

$checks = @($report.Checks)

$checks += [pscustomobject]@{
    Name   = 'perf-cold-start'
    Ok     = ($coldMs -le 3500)
    Detail = "冷启动（全新数据目录）= $coldMs ms（判据 <= 3500）"
}

$checks += [pscustomobject]@{
    Name   = 'perf-warm-start-p50'
    Ok     = ($p50 -le 2000)
    Detail = "暖启动 P50 = $p50 ms（20 次；判据 <= 2000）"
}

$checks += [pscustomobject]@{
    Name   = 'perf-warm-start-p90'
    Ok     = ($p90 -le 2500)
    Detail = "暖启动 P90 = $p90 ms（20 次；判据 <= 2500）"
}

$checks += [pscustomobject]@{
    Name   = 'perf-warm-start-samples'
    Ok     = $true
    Detail = "20 次暖启动：$($warmValues -join ', ') ms"
}

$report.Checks = $checks
$report | Add-Member -NotePropertyName Exe -NotePropertyValue $exe -Force
$report | Add-Member -NotePropertyName FixturesDir -NotePropertyValue $FixturesDir -Force
$report | Add-Member -NotePropertyName ColdStartMs -NotePropertyValue $coldMs -Force
$report | Add-Member -NotePropertyName WarmStartMs -NotePropertyValue @($warmValues) -Force
$report | Add-Member -NotePropertyName WarmP50Ms -NotePropertyValue $p50 -Force
$report | Add-Member -NotePropertyName WarmP90Ms -NotePropertyValue $p90 -Force

Write-Host ''
Write-Host '--- 逐项判定 ---' -ForegroundColor Cyan

foreach ($check in $checks) {
    if ($check.Ok) {
        Write-Host ("[OK]   {0,-32} {1}" -f $check.Name, $check.Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("[FAIL] {0,-32} {1}" -f $check.Name, $check.Detail) -ForegroundColor Red
        $failures += $check.Name
    }
}

# ---------- 4. 落盘报告 ----------

$reportJson = Join-Path $repoRoot 'build\t71-acceptance-report.json'
$json = $report | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($reportJson, $json, [Text.UTF8Encoding]::new($false))

$os = Get-CimInstance Win32_OperatingSystem
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$ramGb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# T7-1 性能达标验收报告')
[void]$md.AppendLine('')
[void]$md.AppendLine("- 时间：$($report.StartedAt)")
[void]$md.AppendLine("- 程序：$exe")
[void]$md.AppendLine("- 机器：$cpu / $ramGb GB RAM / $($os.Caption) build $($os.BuildNumber)")
[void]$md.AppendLine("- 数据目录：$DataDir")
[void]$md.AppendLine("- 冷启动：$coldMs ms（判据 <= 3500）")
[void]$md.AppendLine("- 暖启动：20 次 P50 = $p50 ms / P90 = $p90 ms（判据 <= 2000 / <= 2500）")
[void]$md.AppendLine('')
[void]$md.AppendLine('| 检查项 | 结果 | 实测 |')
[void]$md.AppendLine('|---|---|---|')

foreach ($check in $checks) {
    $state = if ($check.Ok) { 'PASS' } else { 'FAIL' }
    $detail = ($check.Detail -replace '\|', '\|')
    [void]$md.AppendLine("| $($check.Name) | $state | $detail |")
}

$mdPath = Join-Path $repoRoot 'build\t71-acceptance-report.md'
[IO.File]::WriteAllText($mdPath, $md.ToString(), [Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host '报告：' -ForegroundColor DarkGray
Write-Host "  $reportJson" -ForegroundColor Cyan
Write-Host "  $mdPath" -ForegroundColor Cyan

if ($temporary -and -not $KeepDataDir) {
    Write-Host ''
    Write-Host "数据目录保留在 $DataDir（里面有逐项日志与产物，供人工核对；不需要时手动删）" -ForegroundColor DarkGray
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host 'T7-1：全部达标。' -ForegroundColor Green
    exit 0
}

Write-Host "T7-1：未达标 $($failures.Count) 项：$($failures -join '; ')" -ForegroundColor Red
exit 2
