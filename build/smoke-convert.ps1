<#
.SYNOPSIS
    T1-4 转换服务端到端冒烟（含崩溃场景）。

.DESCRIPTION
    两个阶段：
      阶段 1  正式跑一次 --smoke-convert：13 项判据（成功/写库/进度/查重/取消/超时/
              中文路径/磁盘预检/无孤儿进程/无残留临时目录）。
      阶段 2  崩溃场景：起一次转换，等 Calibre 真的开始产出后【强杀本进程】，
              再检查 Calibre 有没有跟着死（Job Object 判据）。

    退出码：0 = 全部通过；2 = 有检查项失败；1 = 冒烟本身跑不起来。

.EXAMPLE
    powershell -File build\smoke-convert.ps1 -File "E:\books\sample.epub"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$File,

    [string]$DataDir,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$KeepDataDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "找不到主程序：$exe" -ForegroundColor Red
    Write-Host "先跑一次 dotnet build -c $Configuration" -ForegroundColor Yellow
    exit 1
}

$source = (Resolve-Path -LiteralPath $File).ProviderPath
if ($source -notlike '*.epub') {
    Write-Host "只支持 .epub：$source" -ForegroundColor Red
    exit 1
}

$temporary = $false
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot ("Data-Test\convert-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $temporary = $true
}

$DataDir = [IO.Path]::GetFullPath($DataDir)
if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }

$script:failed = 0

function Write-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail)

    if ($Ok) {
        Write-Host ("[OK]   {0,-28} {1}" -f $Name, $Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("[FAIL] {0,-28} {1}" -f $Name, $Detail) -ForegroundColor Red
        $script:failed++
    }
}

function Test-UnderTools {
    param([string]$Path)

    $toolsRoot = Join-Path $repoRoot 'Tools'
    return ($Path) -and $Path.StartsWith($toolsRoot, [StringComparison]::OrdinalIgnoreCase)
}

function Get-CalibreProcessCount {
    $count = 0
    foreach ($name in @('ebook-convert', 'python', 'pythonw')) {
        foreach ($p in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            try {
                if (Test-UnderTools $p.Path) { $count++ }
            }
            catch { }
        }
    }
    return $count
}

# ---------- 阶段 1：正式转换冒烟 ----------
Write-Host ""
Write-Host "阶段 1/2：正式转换冒烟（13 项判据）" -ForegroundColor Cyan
Write-Host "源文件：$source"
Write-Host "数据目录：$DataDir"

$arguments = '--smoke-convert="' + $source + '" --datadir="' + $DataDir + '"'
$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait

$reportPath = Join-Path $DataDir 'Temp\convert-smoke.json'
if (-not (Test-Path -LiteralPath $reportPath)) {
    Write-Host "没有生成报告：$reportPath（退出码 $($process.ExitCode)）" -ForegroundColor Red
    exit 1
}

$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
Write-Host ("耗时 {0} ms" -f $report.DurationMs)

foreach ($check in $report.Checks) {
    Write-Check -Name $check.Name -Ok ([bool]$check.Ok) -Detail $check.Detail
}

# ---------- 阶段 2：崩溃场景（Job Object） ----------
Write-Host ""
Write-Host "阶段 2/2：崩溃场景（强杀本进程，验证 Job Object 带走 Calibre）" -ForegroundColor Cyan

$crashData = "$DataDir-crash"
if (Test-Path -LiteralPath $crashData) { Remove-Item -LiteralPath $crashData -Recurse -Force }

$crashArgs = '--smoke-convert-crash="' + $source + '" --datadir="' + $crashData + '"'
$app = Start-Process -FilePath $exe -ArgumentList $crashArgs -PassThru

$marker = Join-Path $crashData 'Temp\convert-crash.started'
$deadline = (Get-Date).AddSeconds(90)
while (-not (Test-Path -LiteralPath $marker) -and (Get-Date) -lt $deadline -and -not $app.HasExited) {
    Start-Sleep -Milliseconds 300
}

$started = Test-Path -LiteralPath $marker
$startedDetail = if ($started) { "标记文件已生成" } else { "90 秒内没等到 Calibre 开始产出" }
Write-Check -Name 'crash-calibre-started' -Ok $started -Detail $startedDetail

Start-Sleep -Seconds 2
$before = Get-CalibreProcessCount

if (-not $app.HasExited) {
    Stop-Process -Id $app.Id -Force
}

try { $app.WaitForExit(10000) | Out-Null } catch { }

Start-Sleep -Seconds 5
$after = Get-CalibreProcessCount

Write-Check -Name 'crash-killed-children' -Ok ($after -eq 0) -Detail ("强杀前 Calibre 进程={0}，强杀后={1}（判据是 0）" -f $before, $after)

# ---------- 汇总 ----------
Write-Host ""
if ($script:failed -eq 0) {
    Write-Host "全部检查通过。" -ForegroundColor Green
}
else {
    Write-Host ("{0} 项失败。" -f $script:failed) -ForegroundColor Red
}

if ($temporary -and -not $KeepDataDir) {
    foreach ($dir in @($DataDir, $crashData)) {
        try { Remove-Item -LiteralPath $dir -Recurse -Force } catch { }
    }
}

if ($script:failed -gt 0) { exit 2 }
exit 0
