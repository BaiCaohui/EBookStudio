<#
.SYNOPSIS
    T2-1 批量导入队列端到端冒烟。

.DESCRIPTION
    跑之前会先在一堆临时目录里造出 201 个文件（200 本合法 EPUB + 1 本坏书 + 1 个 .jpg），
    然后用同一个主程序进程跑完五个场景：
      A 递归扫描并导入 200 本（一本坏书不能卡住队列；导入期间 UI 不能锁死）
      B 同一批再导一次：200 本重复只准弹一次对话框，且不能重复入库
      C 取消：完成当前这本后停，未开始的一本都没动
      D 每个批次都要写进 import_batch 表（活动中心能查到）

    样本不是凭空造的：拿 -File 指定的一本真实 EPUB 当模板，把 zip 条目原样复制，
    再往 META-INF 里塞一个带序号的标记文件，这样每本都是合法 EPUB 且指纹互不相同。

    退出码：0 = 全部通过；2 = 有检查项失败；1 = 冒烟本身跑不起来。

.EXAMPLE
    powershell -File build\smoke-batch-import.ps1 -File "TestCorpus\sample-epub-1.epub"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$File,

    [string]$DataDir,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

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
    Write-Host "先跑一次 dotnet build -c $Configuration" -ForegroundColor Yellow
    exit 1
}

$template = (Resolve-Path -LiteralPath $File).ProviderPath
if ($template -notlike '*.epub') {
    Write-Host "模板必须是一本真实 EPUB：$template" -ForegroundColor Red
    exit 1
}

$temporary = $false
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot ("Data-Test\batch-import-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $temporary = $true
}

$DataDir = [IO.Path]::GetFullPath($DataDir)
if (Test-Path -LiteralPath $DataDir) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}

Write-Host "模板：$template"
Write-Host "主程序：$exe"
Write-Host "数据目录：$DataDir"
Write-Host "正在导入 200 本，请稍等（约 30-60 秒）..."
Write-Host ''# 参数必须整体作为「一个带引号的字符串」传，否则路径里的空格会被当成参数分隔符
$arguments = '--smoke-batch-import="' + $template + '" --datadir="' + $DataDir + '"'

$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait

$reportPath = Join-Path $DataDir 'Temp\batch-import-smoke.json'
if (-not (Test-Path -LiteralPath $reportPath)) {
    Write-Host "没有生成报告：$reportPath（退出码 $($process.ExitCode)）" -ForegroundColor Red
    if (Test-Path -LiteralPath (Join-Path $DataDir 'Logs')) {
        Write-Host '日志尾部：' -ForegroundColor Yellow
        Get-ChildItem -LiteralPath (Join-Path $DataDir 'Logs') -Filter '*.log' |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
            ForEach-Object { Get-Content -LiteralPath $_.FullName -Tail 30 -Encoding UTF8 }
    }

    if ($temporary -and -not $KeepDataDir) {
        try { Remove-Item -LiteralPath $DataDir -Recurse -Force } catch { }
    }

    exit 1
}

$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Host ''
Write-Host ("总耗时 {0} ms" -f $report.DurationMs)
Write-Host ''

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

Write-Host ''
if ($failed -eq 0) {
    Write-Host ("全部 {0} 项通过。" -f $report.Checks.Count) -ForegroundColor Green
}
else {
    Write-Host ("{0} 项失败。" -f $failed) -ForegroundColor Red
}

# 失败时保留数据目录与日志供排查；成功且是临时目录就收干净。
if ($failed -eq 0 -and $temporary -and -not $KeepDataDir) {
    try { Remove-Item -LiteralPath $DataDir -Recurse -Force } catch { }
}
else {
    Write-Host ("数据目录保留在：{0}" -f $DataDir) -ForegroundColor Yellow
}

if ($failed -gt 0) { exit 2 }
exit 0
