# T6-5（书签与笔记）验收驱动脚本。
#
# 它只做四件事：建一个全新的数据目录、跑 App 的 --smoke-t65、把 JSON 报告转成人看的 md。
# 判定全在 App 里（报告 JSON 的 Ok 字段），脚本不重复判。
#
# 不自动构建：先 dotnet build EbookToolSuite.sln -c Debug。
# 本机只有 Windows PowerShell 5.1，所以本文件必须是 UTF-8 带 BOM。
param(
    [string]$Configuration = 'Debug',
    [int]$TimeoutSeconds = 1800
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"

if (-not (Test-Path $exe)) {
    Write-Host "找不到 EXE：$exe（先 dotnet build EbookToolSuite.sln -c $Configuration）" -ForegroundColor Red
    exit 1
}

$dataDir = Join-Path $root 'Data-Test\t65-acceptance'
$corpusDir = Join-Path $root 'Data-Test\t65-corpus'

# 全新数据目录：判据 1/2 的"重启后还在"必须从空库开始才说得清。
if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force }
if (Test-Path $corpusDir) { Remove-Item $corpusDir -Recurse -Force }
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
New-Item -ItemType Directory -Path $corpusDir -Force | Out-Null

Write-Host "T6-5 验收开始：$exe" -ForegroundColor Cyan
Write-Host "  样本目录：$corpusDir"
Write-Host "  数据目录：$dataDir"

$argList = @("--smoke-t65=`"$corpusDir`"", "--datadir=`"$dataDir`"")

$proc = Start-Process -FilePath $exe `
    -ArgumentList $argList `
    -PassThru

if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
    Write-Host "超时（$TimeoutSeconds s），结束进程。" -ForegroundColor Red
    try { $proc.Kill() } catch { }
    exit 1
}

$exitCode = $proc.ExitCode

$reportPath = Join-Path $dataDir 'Temp\t65-smoke.json'

if (-not (Test-Path $reportPath)) {
    Write-Host "没有生成报告：$reportPath（退出码 $exitCode）" -ForegroundColor Red
    exit 1
}

$report = Get-Content -Raw -Path $reportPath | ConvertFrom-Json
$checks = @($report.Checks)
$passed = @($checks | Where-Object { $_.Ok }).Count
$total = $checks.Count
$failed = @($checks | Where-Object { -not $_.Ok })
$failedCount = $failed.Count

$buildDir = Join-Path $root 'build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
Copy-Item $reportPath (Join-Path $buildDir 't65-acceptance-report.json') -Force

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# T6-5 验收报告（书签与笔记）')
[void]$md.AppendLine('')
[void]$md.AppendLine("生成时间：$($report.FinishedAt)")
[void]$md.AppendLine("数据目录：$($report.DataRoot)")
[void]$md.AppendLine("用时：$($report.DurationMs) ms")
[void]$md.AppendLine('')
[void]$md.AppendLine("## 结论：$passed/$total 通过（脚本退出码 " + $(if ($failedCount -gt 0) { '2' } else { '0' }) + '）')
[void]$md.AppendLine('')
[void]$md.AppendLine('| 判据 | 结果 | 说明 |')
[void]$md.AppendLine('| --- | --- | --- |')

foreach ($check in $checks) {
    $state = if ($check.Ok) { '通过' } else { '未通过' }
    $detail = ([string]$check.Detail) -replace '\|', '\|'
    [void]$md.AppendLine("| $($check.Name) | $state | $detail |")
}

$mdPath = Join-Path $buildDir 't65-acceptance-report.md'
[System.IO.File]::WriteAllText($mdPath, $md.ToString(), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "结果：$passed/$total 通过，失败 $failedCount 项" -ForegroundColor $(if ($failedCount -eq 0) { 'Green' } else { 'Yellow' })

foreach ($check in $checks) {
    $mark = if ($check.Ok) { '[ OK ]' } else { '[FAIL]' }
    Write-Host "  $mark $($check.Name) -> $($check.Detail)"
}

Write-Host ""
Write-Host "报告：$mdPath"
Write-Host "JSON：$(Join-Path $buildDir 't65-acceptance-report.json')"

if ($failedCount -gt 0) { exit 2 } else { exit 0 }
