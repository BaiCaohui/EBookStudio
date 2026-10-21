# WebView2 冒烟测试（T0-6 交付物；T0-8 绿色化验证要复用）
#
# 用法：
#   powershell -File build\smoke-webview.ps1
#   powershell -File build\smoke-webview.ps1 -Configuration Release
#
# 退出码：0 = 全过；2 = 有检查项失败；1 = 启动就炸了。

param(
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$exe  = Join-Path $root "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
$data = Join-Path $root 'Data'
$report = Join-Path $data 'Temp\webview-smoke.json'
$errFile = Join-Path $data 'Temp\webview-smoke.error.txt'

if (-not (Test-Path $exe)) {
    Write-Error "找不到 $exe，先 dotnet build。"
    exit 1
}

Remove-Item $report, $errFile -ErrorAction SilentlyContinue

# 每次从干净状态开始，避免旧 userDataFolder 掩盖问题
Remove-Item (Join-Path $data 'WebView2Data') -Recurse -Force -ErrorAction SilentlyContinue

# 注意：参数必须整体作为一个带引号的字符串传。
# Start-Process 不会自动加引号，而 $data 的路径里有空格，
# 不引号会被拆成两个参数（踩过一次）。
$argLine = '--smoke-webview --datadir="' + $data + '"'

Write-Host "启动 $exe"
Write-Host "  参数: $argLine"

$proc = Start-Process -FilePath $exe -ArgumentList $argLine -PassThru
$exited = $proc.WaitForExit(150000)

if (-not $exited) {
    $proc.Kill()
    Write-Error '冒烟测试超过 150 秒未退出，已强制结束。'
    exit 1
}

if (Test-Path $errFile) {
    Write-Host ''
    Write-Host '=== 启动阶段异常 ===' -ForegroundColor Red
    Get-Content $errFile -Encoding UTF8 | Select-Object -First 20
    exit 1
}

if (-not (Test-Path $report)) {
    Write-Error "报告未生成：$report（进程 exit=$($proc.ExitCode)）"
    exit 1
}

$r = Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Host ''
Write-Host ("运行库 {0}   耗时 {1} ms" -f $r.webView2Version, $r.durationMs)
Write-Host ("数据目录 {0}" -f $r.dataRoot)
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