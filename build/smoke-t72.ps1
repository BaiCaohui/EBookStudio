<#
.SYNOPSIS
    T7-2 错误处理与 16 项边界验收。

.DESCRIPTION
    分两段：

      1. 进程内边界（--smoke-t72=<工作目录>）：数据盘拔掉 / 磁盘写满 / 文件被占用 /
         损坏 EPUB / DRM / Zip Slip / XML 注入 / 超长书名 / 特殊字符文件名 /
         工具被删 / 工具输出乱码 / 数据库被占用 / 图片 404 / 主题回退 /
         断电一致性。全部**真的造场景**，不查代码。
      2. 转换中关闭程序（第 12 项）：调用已有的 smoke-queue-crash.ps1，
         先排 10 本转换再强杀自己，然后用同一数据目录重启，验收
         「Job Object 杀干净 + 残留 Running 归位 Queued + 续跑完成」。

    退出码：0 = 全部通过；2 = 有检查项失败；1 = 脚本本身跑不起来。
.EXAMPLE
    powershell -File build\smoke-t72.ps1
#>
[CmdletBinding()]
param(
    [string]$ExePath,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$DataDir,

    [string]$WorkDir,

    [switch]$SkipCrashPhase,

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
    Write-Host "先跑 dotnet build，或用 -ExePath 指定" -ForegroundColor Yellow
    exit 1
}

$temporary = $false
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot ('Data-Test\t72-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $temporary = $true
}
$DataDir = [IO.Path]::GetFullPath($DataDir)

if (Test-Path -LiteralPath $DataDir) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}

if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    $WorkDir = Join-Path $repoRoot ('Data-Test\t72-work-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
$WorkDir = [IO.Path]::GetFullPath($WorkDir)

if (Test-Path -LiteralPath $WorkDir) {
    Remove-Item -LiteralPath $WorkDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

Write-Host ''
Write-Host '===== T7-2 错误处理与边界验收 =====' -ForegroundColor Cyan
Write-Host "程序：$exe"
Write-Host "数据目录：$DataDir（全新）"
Write-Host "工作目录：$WorkDir"
Write-Host ''

# ---------- 1. 进程内 15 项边界 ----------

$reportPath = Join-Path $DataDir 'Temp\t72-smoke.json'
$arguments = '--smoke-t72="' + $WorkDir + '" --datadir="' + $DataDir + '"'

Write-Host '--- 进程内边界（导入 / 文件锁 / 数据库锁 / 阅读器 / 断电）---' -ForegroundColor Cyan

$watch = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru

if (-not $process.WaitForExit(1800000)) {
    $process.Kill()
    Write-Host '进程内边界冒烟超过 30 分钟，已强杀' -ForegroundColor Red
    exit 1
}

$watch.Stop()
$exitCode = $process.ExitCode
Write-Host ("进程退出码={0} 总耗时={1} 秒" -f $exitCode, [math]::Round($watch.Elapsed.TotalSeconds, 1))

if (-not (Test-Path -LiteralPath $reportPath)) {
    Write-Host "没有生成边界报告：$reportPath" -ForegroundColor Red
    Write-Host '（先看日志：' + (Join-Path $DataDir 'Logs') + '）' -ForegroundColor Yellow
    exit 1
}

$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
$checks = @($report.Checks)
$failures = @()

# ---------- 2. 第 12 项：转换中关闭程序（真实强杀 + 重启续跑） ----------

if (-not $SkipCrashPhase) {
    Write-Host ''
    Write-Host '--- 第 12 项：转换中关闭程序（强杀 + 重启续跑）---' -ForegroundColor Cyan

    $samples = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'TestCorpus') -Filter '*.epub' -File -ErrorAction SilentlyContinue)
    if ($samples.Count -eq 0) {
        $checks += [pscustomobject]@{
            Name   = 'boundary-12-crash-resume'
            Ok     = $false
            Detail = 'TestCorpus目录里没有 EPUB，无法验证转换中强杀/续跑'
        }
        $failures += 'boundary-12-crash-resume'
    }
    else {
        $crashData = Join-Path $repoRoot ('Data-Test\t72-crash-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        $crashArgs = @{
            File          = $samples[0].FullName
            DataDir       = $crashData
            Configuration = $Configuration
            ExePath       = $exe
        }

        $crashWatch = [Diagnostics.Stopwatch]::StartNew()
        & (Join-Path $PSScriptRoot 'smoke-queue-crash.ps1') @crashArgs
        $crashExit = $LASTEXITCODE
        $crashWatch.Stop()

        $checks += [pscustomobject]@{
            Name   = 'boundary-12-crash-resume'
            Ok     = ($crashExit -eq 0)
            Detail = "真实强杀 + 重启续跑，smoke-queue-crash.ps1 退出码=$crashExit，" +
                     "耗时=$([math]::Round($crashWatch.Elapsed.TotalSeconds, 1)) 秒"
        }

        if ($crashExit -ne 0) {
            $failures += 'boundary-12-crash-resume'
        }

        if (-not $KeepDataDir -and (Test-Path -LiteralPath $crashData)) {
            Remove-Item -LiteralPath $crashData -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
else {
    $checks += [pscustomobject]@{
        Name   = 'boundary-12-crash-resume'
        Ok     = $false
        Detail = '本次用 -SkipCrashPhase 跳过了真实强杀阶段，未验证第 12 项'
    }
    $failures += 'boundary-12-crash-resume'
}

# ---------- 3. 判定与报告 ----------

Write-Host ''
Write-Host '--- 逐项判定 ---' -ForegroundColor Cyan

foreach ($check in $checks) {
    if ($check.Ok) {
        Write-Host ("[OK]   {0,-40} {1}" -f $check.Name, $check.Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("[FAIL] {0,-40} {1}" -f $check.Name, $check.Detail) -ForegroundColor Red
        if ($failures -notcontains $check.Name) {
            $failures += $check.Name
        }
    }
}

# 判据 2：错误目录必须覆盖全部 ErrorCode（由 App 内的检查项证明）；
# 判据 4：原子写审查由 build\atomic-write-audit.ps1 单独跑，结果并入报告。
$auditScript = Join-Path $PSScriptRoot 'atomic-write-audit.ps1'
if (Test-Path -LiteralPath $auditScript) {
    Write-Host ''
    Write-Host '--- 判据 4：原子写静态审查 ---' -ForegroundColor Cyan

    $audit = & $auditScript
    $auditOk = ($LASTEXITCODE -eq 0)

    $checks += [pscustomobject]@{
        Name   = 'criterion-04-atomic-writes'
        Ok     = $auditOk
        Detail = if ($auditOk) { '生产代码写文件全部走临时文件 + 原子替换' } else { '发现非原子写，详见 atomic-write-audit 输出' }
    }

    if (-not $auditOk) {
        $failures += 'criterion-04-atomic-writes'
    }
}

$report.Checks = $checks
$report | Add-Member -NotePropertyName Exe -NotePropertyValue $exe -Force
$report | Add-Member -NotePropertyName WorkDir -NotePropertyValue $WorkDir -Force

$reportJson = Join-Path $repoRoot 'build\t72-acceptance-report.json'
$json = $report | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($reportJson, $json, [Text.UTF8Encoding]::new($false))

$os = Get-CimInstance Win32_OperatingSystem
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$ramGb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# T7-2 错误处理与边界验收报告')
[void]$md.AppendLine('')
[void]$md.AppendLine("- 时间：$($report.StartedAt)")
[void]$md.AppendLine("- 程序：$exe")
[void]$md.AppendLine("- 机器：$cpu / $ramGb GB RAM / $($os.Caption) build $($os.BuildNumber)")
[void]$md.AppendLine("- 数据目录：$DataDir")
[void]$md.AppendLine("- 工作目录：$WorkDir")
[void]$md.AppendLine('')
[void]$md.AppendLine('| 检查项 | 结果 | 实测 |')
[void]$md.AppendLine('|---|---|---|')

foreach ($check in $checks) {
    $state = if ($check.Ok) { 'PASS' } else { 'FAIL' }
    $detail = ($check.Detail -replace '\|', '\|')
    [void]$md.AppendLine("| $($check.Name) | $state | $detail |")
}

$mdPath = Join-Path $repoRoot 'build\t72-acceptance-report.md'
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
    Write-Host 'T7-2：全部通过。' -ForegroundColor Green
    exit 0
}

Write-Host "T7-2：失败 $($failures.Count) 项：$($failures -join '; ')" -ForegroundColor Red
exit 2
