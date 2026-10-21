# T6-3（漫画 CBZ/CBR）验收驱动脚本。
#
# 它只做四件事：建一个全新的数据目录、跑 App 的 --smoke-t63、把 JSON 报告转成人看的 md、
# 把截图挪到 build\t63-shots。判定全在 App 里（报告 JSON 的 Ok 字段），脚本不重复判。
#
# 不自动构建：先 dotnet build EbookToolSuite.sln -c Debug。
# 本机只有 Windows PowerShell 5.1，所以本文件必须是 UTF-8 带 BOM。
param(
    [string]$Configuration = 'Debug',
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"

if (-not (Test-Path $exe)) {
    Write-Host "找不到 EXE：$exe（先 dotnet build EbookToolSuite.sln -c $Configuration）" -ForegroundColor Red
    exit 1
}

$dataDir = Join-Path $root 'Data-Test\t63-acceptance'
$corpusDir = Join-Path $root 'Data-Test\t63-corpus'

# 全新数据目录是判据的一部分：Data\Cache\Pages 必须从"不存在"开始。
if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force }
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
New-Item -ItemType Directory -Path $corpusDir -Force | Out-Null

Write-Host "T6-3 验收开始：$exe" -ForegroundColor Cyan
Write-Host "  样本目录：$corpusDir"
Write-Host "  数据目录：$dataDir"

$proc = Start-Process -FilePath $exe `
    -ArgumentList "--smoke-t63=`"$corpusDir`"", "--datadir=`"$dataDir`"" `
    -PassThru

if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
    Write-Host "超时（$TimeoutSeconds s），结束进程。" -ForegroundColor Red
    try { $proc.Kill() } catch { }
    exit 1
}

$exitCode = $proc.ExitCode

$reportPath = Join-Path $dataDir 'Temp\t63-smoke.json'

if (-not (Test-Path $reportPath)) {
    Write-Host "没有生成报告：$reportPath（退出码 $exitCode）" -ForegroundColor Red
    exit 1
}

$report = Get-Content -Raw -Path $reportPath | ConvertFrom-Json
$checks = @($report.Checks)
$passed = @($checks | Where-Object { $_.Ok }).Count
$total = $checks.Count

# 两项按用户 2026-10-17 的口径不算硬失败：CBR 本机没有真实样本，自动切白边是规格里的"可选"。
# 报告里照原样写 Ok=false，只是不让它们把整轮判成失败（T6-2B 对"未实现项"也是这么处理的）。
$waived = @('J2-cbr-open', 'J6-auto-trim')
$hardFailures = @($checks | Where-Object { (-not $_.Ok) -and ($waived -notcontains $_.Name) })
$hardFailedCount = $hardFailures.Count

$buildDir = Join-Path $root 'build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
Copy-Item $reportPath (Join-Path $buildDir 't63-acceptance-report.json') -Force

# 截图：从临时目录搬到 build，方便贴进对话/文档。
$srcShots = Join-Path $dataDir 'Temp\t63-shots'
$dstShots = Join-Path $buildDir 't63-shots'

if (Test-Path $srcShots) {
    if (Test-Path $dstShots) { Remove-Item $dstShots -Recurse -Force }
    New-Item -ItemType Directory -Path $dstShots -Force | Out-Null
    Get-ChildItem -Path $srcShots -Filter '*.png' | Copy-Item -Destination $dstShots -Force
}

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# T6-3 验收报告（漫画 CBZ/CBR）')
[void]$md.AppendLine('')
[void]$md.AppendLine("生成时间：$($report.FinishedAt)")
[void]$md.AppendLine("数据目录：$($report.DataRoot)")
[void]$md.AppendLine("用时：$($report.DurationMs) ms")
[void]$md.AppendLine('')
[void]$md.AppendLine("## 结论：$passed/$total 通过，硬失败 $hardFailedCount 项（脚本退出码 " + $(if ($hardFailedCount -gt 0) { '2' } else { '0' }) + '）')
[void]$md.AppendLine('')
[void]$md.AppendLine('| 判据 | 结果 | 说明 |')
[void]$md.AppendLine('| --- | --- | --- |')

foreach ($check in $checks) {
    $state = if ($check.Ok) { '通过' } elseif ($waived -contains $check.Name) { 'WAIV（未检验/未实现）' } else { '未通过' }
    $detail = ([string]$check.Detail) -replace '\|', '\|'
    [void]$md.AppendLine("| $($check.Name) | $state | $detail |")
}

[void]$md.AppendLine('')
[void]$md.AppendLine('## 截图')
[void]$md.AppendLine('')

if (Test-Path $dstShots) {
    Get-ChildItem -Path $dstShots -Filter '*.png' | Sort-Object Name | ForEach-Object {
        [void]$md.AppendLine("- ``build\t63-shots\$($_.Name)``")
    }
}

$mdPath = Join-Path $buildDir 't63-acceptance-report.md'
[System.IO.File]::WriteAllText($mdPath, $md.ToString(), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "结果：$passed/$total 通过，硬失败 $hardFailedCount 项（WAIV = 按约定记为未检验/未实现）" -ForegroundColor $(if ($hardFailedCount -eq 0) { 'Green' } else { 'Yellow' })

foreach ($check in $checks) {
    $mark = if ($check.Ok) { '[ OK ]' } elseif ($waived -contains $check.Name) { '[WAIV]' } else { '[FAIL]' }
    Write-Host "  $mark $($check.Name) -> $($check.Detail)"
}

Write-Host ""
Write-Host "报告：$mdPath"
Write-Host "JSON：$(Join-Path $buildDir 't63-acceptance-report.json')"

if ($hardFailedCount -gt 0) { exit 2 } else { exit 0 }
