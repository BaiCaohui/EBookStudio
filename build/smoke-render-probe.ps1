# 渲染探针驱动脚本（T0-9 交付物）
#
# 用法：
#   powershell -File build\smoke-render-probe.ps1 -File '.\Data\Temp\t09\small.mobi'
#   powershell -File build\smoke-render-probe.ps1 -File '.\Data\Temp\t09\sample.txt' -ExpectRendered
#
# 判定标准：报告里的 renderedAsDocument 是否等于 -ExpectRendered。
#   MOBI / AZW3 / EPUB  预期"渲染不了"（Chromium 不认，触发下载）-> 不加开关
#   TXT / HTML / PDF    预期"能渲染"                              -> 加 -ExpectRendered
#
# 退出码：0 = 符合预期；2 = 不符合预期；1 = 跑不起来。

param(
    [Parameter(Mandatory = $true)][string]$File,
    [switch]$ExpectRendered,
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$root   = Split-Path $PSScriptRoot -Parent
$exe    = Join-Path $root "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
$data   = Join-Path $root 'Data'
$report = Join-Path $data 'Temp\render-probe.json'

if (-not (Test-Path $exe)) {
    Write-Error "找不到 $exe，先 dotnet build。"
    exit 1
}

$target = (Resolve-Path $File).Path
Write-Host ("探针目标 {0}（{1:N1} KB）" -f $target, ((Get-Item $target).Length / 1KB))

Remove-Item $report -ErrorAction SilentlyContinue

# 参数必须整体作为一个带引号的字符串传。Start-Process 不会自动加引号，
# 而目标路径和数据目录里都有空格，不引号会被拆成多个参数。
$argLine = '--render-probe="' + $target + '" --datadir="' + $data + '"'

$proc = Start-Process -FilePath $exe -ArgumentList $argLine -PassThru
if (-not $proc.WaitForExit(180000)) {
    $proc.Kill()
    Write-Error '探针超过 180 秒未退出，已强制结束。'
    exit 1
}

if (-not (Test-Path $report)) {
    Write-Error "报告未生成：$report（进程 exit=$($proc.ExitCode)）"
    exit 1
}

$r = Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Host ''
Write-Host ("运行库      {0}" -f $r.webView2Version)
Write-Host ("导航完成    {0}" -f $r.navigationCompleted)
Write-Host ("导航成功    {0}" -f $r.navigationSuccess)
Write-Host ("导航错误    {0}" -f $r.navigationError)
Write-Host ("内容类型    {0}" -f $r.contentType)
Write-Host ("正文长度    {0}" -f $r.bodyTextLength)
Write-Host ("触发下载    {0}  文件名={1}" -f $r.downloadTriggered, $r.downloadSuggestedName)
Write-Host ("耗时        {0} ms" -f $r.durationMs)
$verdict = if ($r.renderedAsDocument) { '可渲染' } else { '不可渲染' }
Write-Host ("判定        {0}" -f $verdict)
Write-Host ''
Write-Host '备注：'
foreach ($n in $r.notes) { Write-Host ("  - {0}" -f $n) }
Write-Host ''

$actual   = [bool]$r.renderedAsDocument
$expected = [bool]$ExpectRendered

if ($actual -eq $expected) {
    Write-Host ("符合预期（预期可渲染={0}）。" -f $expected) -ForegroundColor Green
    exit 0
}

Write-Host ("不符合预期：预期可渲染={0}，实际={1}" -f $expected, $actual) -ForegroundColor Red
exit 2