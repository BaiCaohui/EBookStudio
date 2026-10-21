# 日志与全局异常冒烟测试（T0-7 交付物）
#
# 验证三件事：
#   1. UI 线程未处理异常不闪退，日志里有完整堆栈
#   2. 未观察的 Task 异常会被捕获
#   3. 日志按天生成、格式正确、中文不乱码、无 BOM
#
# 注意：本文件必须保持 UTF-8 带 BOM。PowerShell 5.1 在没有 BOM 时
# 会按 ANSI(GBK) 读 .ps1，中文全部乱码且解析失败。

param(
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$exe  = Join-Path $root "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
$data = Join-Path $root 'Data'
$report = Join-Path $data 'Temp\logging-smoke.json'

if (-not (Test-Path $exe)) {
    Write-Error "找不到 $exe，先 dotnet build。"
    exit 1
}

Remove-Item $report -ErrorAction SilentlyContinue
Remove-Item (Join-Path $data 'Logs\*.log') -ErrorAction SilentlyContinue

# 参数必须整体作为一个带引号的字符串传：Start-Process 不会自动加引号，
# 而 $data 路径含空格，不引号会被拆成两个参数。
$argLine = '--smoke-logging --datadir="' + $data + '"'

Write-Host "启动 $exe"

$proc = Start-Process -FilePath $exe -ArgumentList $argLine -PassThru
$exited = $proc.WaitForExit(180000)

if (-not $exited) {
    $proc.Kill()
    Write-Error '日志冒烟测试超过 180 秒未退出。'
    exit 1
}

if (-not (Test-Path $report)) {
    Write-Error "报告未生成：$report（进程 exit=$($proc.ExitCode)）"
    exit 1
}

$r = Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Host ''
Write-Host ("数据目录 {0}   耗时 {1} ms" -f $r.dataRoot, $r.durationMs)
Write-Host ''

foreach ($c in $r.checks) {
    $tag = if ($c.ok) { '[OK]  ' } else { '[FAIL]' }
    $color = if ($c.ok) { 'Gray' } else { 'Red' }
    Write-Host ("{0} {1,-26} {2}" -f $tag, $c.name, $c.detail) -ForegroundColor $color
}

Write-Host ''
if ($r.passed) {
    Write-Host ("全部 {0} 项通过。" -f $r.checks.Count) -ForegroundColor Green
    exit 0
}

Write-Host '存在失败项。' -ForegroundColor Red
exit 2