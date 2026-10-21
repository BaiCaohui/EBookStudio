# T6-2B 移动端图片通道探针驱动脚本
#
# 用法（本机只有 Windows PowerShell 5.1，没有 pwsh）：
#   powershell -ExecutionPolicy Bypass -File build\smoke-t62b-probe.ps1
#   powershell -ExecutionPolicy Bypass -File build\smoke-t62b-probe.ps1 -Pdf 'E:\...\sample.pdf' -Configuration Release
#
# 它做四件事：
#   1) 起 EBookStudio.exe --t62b-probe="<pdf 绝对路径>" --datadir="<Data>"
#   2) 等退出，打印退出码
#   3) 打印 Data\Temp\t62b-probe.json 全文
#   4) 归档一份到 build\t62b-probe-report.json
#
# 退出码：透传 EXE 的退出码（0 = 三问都拿到数字；2 = 断言不成立；1 = 跑不起来）。

param(
    [string]$Pdf = '.\TestCorpus\sample-pdf-1.pdf',
    [string]$Configuration = 'Debug',
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$exe  = Join-Path $root "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
$data = Join-Path $root 'Data'

if (-not (Test-Path $exe)) {
    Write-Error "找不到 $exe，先 dotnet build EbookToolSuite.sln -c $Configuration。"
    exit 1
}

if (-not (Test-Path -LiteralPath $Pdf)) {
    Write-Error "找不到 PDF：$Pdf"
    exit 1
}

$pdfPath = (Resolve-Path -LiteralPath $Pdf).Path
$info = Get-Item -LiteralPath $pdfPath

Write-Host ("PDF  {0}" -f $info.Name)
Write-Host ("     {0:N1} MB" -f ($info.Length / 1MB))
Write-Host ("Data {0}" -f $data)
Write-Host ''

$jsonIn  = Join-Path $data 'Temp\t62b-probe.json'
$jsonOut = Join-Path $root 'build\t62b-probe-report.json'

Remove-Item -LiteralPath $jsonIn -ErrorAction SilentlyContinue

# 参数必须整体作为一个带引号的字符串传：路径与数据目录都含空格与括号。
$argLine = '--t62b-probe="' + $pdfPath + '" --datadir="' + $data + '"'

Write-Host "启动探针（最多 $TimeoutSeconds 秒）..."
$proc = Start-Process -FilePath $exe -ArgumentList $argLine -PassThru

if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
    $proc.Kill()
    Write-Error "探针超过 $TimeoutSeconds 秒未退出，已强制结束。"
    exit 1
}

$code = $proc.ExitCode
Write-Host "探针进程 exit=$code"
Write-Host ''

if (-not (Test-Path -LiteralPath $jsonIn)) {
    Write-Error "报告未生成：$jsonIn（进程 exit=$code，先看 Data\Logs 里的日志）"
    exit 1
}

Copy-Item -LiteralPath $jsonIn -Destination $jsonOut -Force

$raw = Get-Content -LiteralPath $jsonIn -Raw -Encoding UTF8

Write-Host '---- Data\Temp\t62b-probe.json ----'
Write-Host $raw
Write-Host '-----------------------------------'
Write-Host ''
Write-Host ("归档 -> {0}" -f $jsonOut)

exit $code
