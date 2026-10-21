<#
.SYNOPSIS
    T2-4 判据 b：崩溃恢复端到端冒烟（两阶段）。
.DESCRIPTION
    阶段 1：造 10 份样本 -> 导入 -> 一次排 10 本转 MOBI -> 等 Calibre 真跑起来 -> 强杀自己。
    阶段 2：用同一个数据目录再启动一次，验收「残留的 Running 归位成排队并接着跑完」。
    判据：crash-marker-read / resume-completed / resume-was-needed /
          resume-no-active-left / resume-no-processes。
    退出码：0 = 全部通过；2 = 有检查项失败；1 = 冒烟本身跑不起来。
.EXAMPLE
    powershell -File build\smoke-queue-crash.ps1 -File "TestCorpus\sample-epub-1.epub"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$File,
    [string]$DataDir,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$ExePath,
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
    exit 1
}
$template = (Resolve-Path -LiteralPath $File).ProviderPath
$temporary = $false
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot ("Data-Test\queue-crash-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $temporary = $true
}

$DataDir = [IO.Path]::GetFullPath($DataDir)
if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }

$corpus = Join-Path ([IO.Path]::GetTempPath()) ("ets-crash-corpus-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $corpus | Out-Null

$lf = [string][char]10
$bytes = [IO.File]::ReadAllBytes($template)
for ($i = 1; $i -le 10; $i++) {
    $trailer = [Text.Encoding]::ASCII.GetBytes($lf + "ETS-CRASH-SMOKE-" + $i.ToString('D3') + $lf)
    $target = Join-Path $corpus ("book-{0:D2}.epub" -f $i)
    $ms = New-Object System.IO.MemoryStream
    $ms.Write($bytes, 0, $bytes.Length)
    $ms.Write($trailer, 0, $trailer.Length)
    [IO.File]::WriteAllBytes($target, $ms.ToArray())
    $ms.Dispose()
}

Write-Host "模板：$template"
Write-Host "主程序：$exe"
Write-Host "数据目录：$DataDir"
Write-Host "样本：$corpus（10 份）"
Write-Host ''

$phase1Args = '--smoke-queue-crash="' + $corpus + '" --datadir="' + $DataDir + '"'
Write-Host '阶段 1：排队后强杀自己...' -ForegroundColor Cyan
$p1 = Start-Process -FilePath $exe -ArgumentList $phase1Args -PassThru -Wait

$markerPath = Join-Path $DataDir 'Temp\queue-crash-marker.json'
$failed = 0

Write-Host ''
if ((-not $p1.ExitCode.Equals(0)) -and (Test-Path -LiteralPath $markerPath)) {
    Write-Host ("[OK]   {0,-32} {1}" -f 'phase1-crashed', "退出码 $($p1.ExitCode)，现场已留下") -ForegroundColor Green
}
else {
    Write-Host ("[FAIL] {0,-32} {1}" -f 'phase1-crashed', "退出码 $($p1.ExitCode)，现场 $markerPath") -ForegroundColor Red
    $failed++
}

Write-Host '阶段 2：同一个数据目录重启，等它恢复并跑完...' -ForegroundColor Cyan
$phase2Args = '--smoke-queue-resume --datadir="' + $DataDir + '"'
$p2 = Start-Process -FilePath $exe -ArgumentList $phase2Args -PassThru -Wait
$reportPath = Join-Path $DataDir 'Temp\convert-queue-smoke.json'
if (-not (Test-Path -LiteralPath $reportPath)) {
    Write-Host "没有生成报告：$reportPath（阶段 2 退出码 $($p2.ExitCode)）" -ForegroundColor Red
    try { Remove-Item -LiteralPath $corpus -Recurse -Force } catch { }
    exit 1
}

Write-Host ''
$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($check in $report.Checks) {
    if ($check.Ok) {
        Write-Host ("[OK]   {0,-32} {1}" -f $check.Name, $check.Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("[FAIL] {0,-32} {1}" -f $check.Name, $check.Detail) -ForegroundColor Red
        $failed++
    }
}

try { Remove-Item -LiteralPath $corpus -Recurse -Force } catch { }
Write-Host ''
if ($failed -eq 0) {
    Write-Host '恢复冒烟全部通过。' -ForegroundColor Green
    if ($temporary -and -not $KeepDataDir) {
        Remove-Item -LiteralPath $DataDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    exit 0
}

Write-Host ("{0} 项失败，数据目录保留在：{1}" -f $failed, $DataDir) -ForegroundColor Red
exit 2
