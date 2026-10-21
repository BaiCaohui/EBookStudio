<#
.SYNOPSIS
    阶段 3「书籍编辑 UI 入口」端到端冒烟。

.DESCRIPTION
    用真实 EPUB 跑：三个入口都在 -> 点工具栏「编辑」真的开窗 -> 工作区与表单灌上真实元数据
    -> 改书名 + 换封面 -> 保存 -> 磁盘上的 EPUB 变了且打包自检通过 -> 库里文件信息与封面缓存跟上
    -> 关窗清理工作区 -> 源文件一个字节都没动。

    退出码：0 = 全部通过；2 = 有检查项失败；1 = 冒烟本身跑不起来。

.EXAMPLE
    powershell -File build\smoke-edit.ps1 -File "E:\books\sample.epub"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$File,

    [string]$DataDir,

    # 直接指定被测的可执行文件（发布产物验收用；不传就用 src 下的构建产物）
    [string]$ExePath,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # 看门狗：程序若有模态弹窗卡住，最多等这么久再强杀，绝不让控制台挂死
    [int]$TimeoutSeconds = 300,

    [switch]$KeepDataDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $exe = Join-Path $repoRoot "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
}
else {
    $exe = (Resolve-Path -LiteralPath $ExePath).ProviderPath
}

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
    $DataDir = Join-Path $repoRoot ("Data-Test\edit-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $temporary = $true
}

$DataDir = [IO.Path]::GetFullPath($DataDir)
if (Test-Path -LiteralPath $DataDir) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}

Write-Host "源文件：$source"
Write-Host "数据目录：$DataDir"

# 参数必须整体作为「一个带引号的字符串」传，否则路径里的空格会被当成参数分隔符
$arguments = '--smoke-edit="' + $source + '" --datadir="' + $DataDir + '"'

$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
}

if (-not $process.HasExited) {
    Write-Host "超过 $TimeoutSeconds 秒还没退出（疑似卡在模态弹窗），强制结束。" -ForegroundColor Red
    try { $process.Kill() } catch { }
    exit 1
}

$reportPath = Join-Path $DataDir 'Temp\edit-smoke.json'
if (-not (Test-Path -LiteralPath $reportPath)) {
    Write-Host "没有生成报告：$reportPath（退出码 $($process.ExitCode)）" -ForegroundColor Red
    exit 1
}

$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Host ''
Write-Host ("耗时 {0} ms" -f $report.DurationMs)
Write-Host ''

$failed = 0
foreach ($check in $report.Checks) {
    if ($check.Ok) {
        Write-Host ("[OK]   {0,-32} {1}" -f $check.Name, $check.Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("[FAIL] {0,-32} {1}" -f $check.Name, $check.Detail) -ForegroundColor Red
        $failed++
    }
}

Write-Host ''
if ($failed -eq 0) {
    Write-Host ("全部 {0} 项通过。" -f $report.Checks.Count) -ForegroundColor Green
}
else {
    Write-Host ("{0} 项失败。" -f $failed) -ForegroundColor Red
}

if ($temporary -and -not $KeepDataDir) {
    # 数据目录里有被编辑过的书，删掉不留垃圾
    try { Remove-Item -LiteralPath $DataDir -Recurse -Force } catch { }
}

if ($failed -gt 0) { exit 2 }
exit 0
