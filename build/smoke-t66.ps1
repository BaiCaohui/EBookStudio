# T6-6「阶段 6 验收」驱动脚本
#
# 用法：
#   powershell -File build\smoke-t66.ps1
#   powershell -File build\smoke-t66.ps1 -PdfDir '.\TestCorpus' -FixturesRoot 'Data-Test'
#
# 它做四件事：
#   1) 用一个全新的 datadir（默认 Data-Test\t66-acceptance，先删再建）——六条场景从零开始；
#   2) 起 EBookStudio.exe --smoke-t66="<PDF 样本目录>" --t66-fixtures="<样本根>" --datadir="<新 Data>"；
#   3) 把 <Data>\Temp\t66-smoke.json 收成 build\t66-acceptance-report.json 并生成 .md；
#   4) 打印每一项判定。
#
# 退出码：0 = 没有硬失败（WAIV 项除外）；2 = 有硬失败；1 = 跑不起来。
#
# WAIV（已知限制，不算硬失败，但报告里如实写）：
#   · J2-pdf-100mb —— 100 MB 扫描件"点开到首页可见 ≤5 s"未达标（T6-2 判据④同一限制，
#     用户 2026-10-13 已接受；瓶颈是 pdfium 解析非线性化 PDF）。

param(
    [string]$PdfDir = '.\TestCorpus',
    [string]$FixturesRoot = 'Data-Test',
    [string]$DataDir = 'Data-Test\t66-acceptance',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [int]$TimeoutSeconds = 900,
    [switch]$KeepDataDir
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
$data = Join-Path $root $DataDir
$pdf = Join-Path $root $PdfDir
$fixtures = Join-Path $root $FixturesRoot

if (-not (Test-Path $exe)) {
    Write-Error "找不到 $exe，先 dotnet build。"
    exit 1
}

if (-not (Test-Path $pdf)) {
    Write-Error "找不到 PDF 样本目录 $pdf。"
    exit 1
}

if (-not (Test-Path $fixtures)) {
    Write-Error "找不到样本根 $fixtures（应含 t61-fixtures 与 t63-corpus）。"
    exit 1
}

if (Test-Path $data) {
    Remove-Item -LiteralPath $data -Recurse -Force
}

New-Item -ItemType Directory -Path $data -Force | Out-Null

Write-Host "T6-6 验收开始：$exe"
Write-Host "  PDF 样本：$pdf"
Write-Host "  样本根  ：$fixtures"
Write-Host "  数据目录：$data"
Write-Host ''

$arguments = @(
    "--smoke-t66=`"$pdf`"",
    "--t66-fixtures=`"$fixtures`"",
    "--datadir=`"$data`""
)

$proc = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)

while (-not $proc.HasExited -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
}

if (-not $proc.HasExited) {
    try { $proc.Kill() } catch { }
    Write-Host "超时（$TimeoutSeconds 秒），已强杀。" -ForegroundColor Red
    exit 1
}

$reportPath = Join-Path $data 'Temp\t66-smoke.json'

if (-not (Test-Path $reportPath)) {
    Write-Host "没有生成报告：$reportPath（退出码 $($proc.ExitCode)）" -ForegroundColor Red
    exit 1
}

$report = Get-Content -Raw -Path $reportPath | ConvertFrom-Json
$checks = @($report.Checks)

# 已知限制：100 MB 首屏 >5 s（用户 2026-10-13 接受），记为 WAIV 而不是硬失败。
$waiv = @('J2-pdf-100mb')

$passed = @($checks | Where-Object { $_.Ok }).Count
$total = $checks.Count
$failed = @($checks | Where-Object { -not $_.Ok })
$hard = @($failed | Where-Object { $waiv -notcontains $_.Name })
$waived = @($failed | Where-Object { $waiv -contains $_.Name })

$buildDir = Join-Path $root 'build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
Copy-Item $reportPath (Join-Path $buildDir 't66-acceptance-report.json') -Force

$markdown = New-Object System.Text.StringBuilder
[void]$markdown.AppendLine('# T6-6 验收报告（阶段 6 汇总）')
[void]$markdown.AppendLine('')
[void]$markdown.AppendLine("生成时间：$($report.FinishedAt)")
[void]$markdown.AppendLine("数据目录：$($report.DataRoot)")
[void]$markdown.AppendLine("用时：$($report.DurationMs) ms")
[void]$markdown.AppendLine('')
[void]$markdown.AppendLine("## 结论：$passed/$total 通过；硬失败 $($hard.Count) 项，已知限制 WAIV $($waived.Count) 项")
[void]$markdown.AppendLine('')
[void]$markdown.AppendLine('| 判据 | 结果 | 说明 |')
[void]$markdown.AppendLine('| --- | --- | --- |')

foreach ($check in $checks) {
    if ($check.Ok) {
        $state = '通过'
    }
    elseif ($waiv -contains $check.Name) {
        $state = 'WAIV（已知限制）'
    }
    else {
        $state = '未通过'
    }

    $detail = ([string]$check.Detail) -replace '\|', '\|'
    [void]$markdown.AppendLine("| $($check.Name) | $state | $detail |")
}

$mdPath = Join-Path $buildDir 't66-acceptance-report.md'
[System.IO.File]::WriteAllText($mdPath, $markdown.ToString(), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host "结果：$passed/$total 通过，硬失败 $($hard.Count) 项，WAIV $($waived.Count) 项" -ForegroundColor $(if ($hard.Count -eq 0) { 'Green' } else { 'Yellow' })

foreach ($check in $checks) {
    if ($check.Ok) {
        $mark = '[ OK ]'
    }
    elseif ($waiv -contains $check.Name) {
        $mark = '[WAIV]'
    }
    else {
        $mark = '[FAIL]'
    }

    Write-Host "  $mark $($check.Name) -> $($check.Detail)"
}

Write-Host ''
Write-Host "报告：$mdPath"
Write-Host "JSON：$(Join-Path $buildDir 't66-acceptance-report.json')"

if ($hard.Count -gt 0) {
    exit 2
}

exit 0
