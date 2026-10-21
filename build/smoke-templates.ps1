# 转换模板冒烟（执行计划 T2-5）。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File build\smoke-templates.ps1 `
#       -Epub "TestCorpus\sample-epub-1.epub" `
#       -Epub2 "TestCorpus\Sample Book A (Sample Author) ...-1.epub"
#
# 退出码：0 = 全部通过；2 = 有判据失败；1 = 跑不起来（参数/环境问题）。
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Epub,
    [Parameter(Mandatory = $true)][string]$Epub2,
    [string]$DataDir,
    [string]$ExePath,
    [ValidateSet("Debug", "Release")][string]$Configuration = "Debug",
    [switch]$KeepDataDir
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-FullPath([string]$p) {
    if ([IO.Path]::IsPathRooted($p)) { return [IO.Path]::GetFullPath($p) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $p))
}

$epubFull = Resolve-FullPath $Epub
$epub2Full = Resolve-FullPath $Epub2

if (-not (Test-Path -LiteralPath $epubFull -PathType Leaf)) {
    Write-Host "找不到 EPUB 样本：$epubFull" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $epub2Full -PathType Leaf)) {
    Write-Host "找不到第二本 EPUB 样本：$epub2Full" -ForegroundColor Red
    exit 1
}

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $exe = Join-Path $repoRoot "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
}
else {
    $exe = Resolve-FullPath $ExePath
}

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    Write-Host "找不到程序：$exe" -ForegroundColor Red
    exit 1
}
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $dataRoot = Join-Path $repoRoot ("Data-Test\template-smoke-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
}
else {
    $dataRoot = Resolve-FullPath $DataDir
}

if (Test-Path -LiteralPath $dataRoot) {
    if ($KeepDataDir) {
        Write-Host "复用已有数据目录：$dataRoot"
    }
    else {
        Remove-Item -LiteralPath $dataRoot -Recurse -Force
    }
}

New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
$reportFile = Join-Path $dataRoot "Temp\template-smoke.json"

# 参数必须整体作为一个带引号的字符串传：路径里有空格与中文。
$q = [char]0x22
$argLine = "--smoke-templates-epub=$q$epubFull$q --smoke-templates-b=$q$epub2Full$q --datadir=$q$dataRoot$q"

Write-Host "样本 EPUB：$epubFull"
Write-Host "样本 EPUB-B：$epub2Full"
Write-Host "数据目录 ：$dataRoot"
Write-Host "程序     ：$exe"
Write-Host ""

$sw = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $exe -ArgumentList $argLine -PassThru -Wait
$sw.Stop()

Write-Host ("程序退出码 {0}，用时 {1:N1} 秒" -f $proc.ExitCode, $sw.Elapsed.TotalSeconds)
Write-Host ""
if (-not (Test-Path -LiteralPath $reportFile -PathType Leaf)) {
    Write-Host "没有生成冒烟报告：$reportFile" -ForegroundColor Red
    Write-Host "（程序可能没启动起来，或提前退出了；看上面的退出码）" -ForegroundColor Red
    exit 1
}

$report = Get-Content -LiteralPath $reportFile -Raw -Encoding UTF8 | ConvertFrom-Json

$failed = 0
foreach ($check in $report.Checks) {
    if ($check.Ok) {
        Write-Host ("  [OK]   {0,-38} {1}" -f $check.Name, $check.Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("  [FAIL] {0,-38} {1}" -f $check.Name, $check.Detail) -ForegroundColor Red
        $failed++
    }
}

$total = @($report.Checks).Count
Write-Host ""
Write-Host ("判据 {0}/{1} 通过" -f ($total - $failed), $total)
Write-Host "数据目录保留在：$dataRoot"
Write-Host ""

if ($failed -gt 0) { exit 2 }

exit 0