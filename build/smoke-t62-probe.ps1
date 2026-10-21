# T6-2 PDF 探针驱动脚本（设计文档 §6.5.3 九项）
#
# 用法：
#   pwsh -File build\smoke-t62-probe.ps1
#   pwsh -File build\smoke-t62-probe.ps1 -PdfDir '.\TestCorpus' -Configuration Release
#
# 它做三件事：
#   1) 起 EBookStudio.exe --t62-probe="<样本目录>" --datadir="<Data>"
#   2) 把 Data\Temp\t62-probe.md / .json / t62-probe-shots\ 收进 build\
#   3) 打印九项判定
#
# 退出码：0 = 九项都跑出了结论；2 = 有项没跑出来（含抛异常）；1 = 跑不起来。

param(
    [string]$PdfDir = '.\TestCorpus',
    [string]$Configuration = 'Debug',
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$exe  = Join-Path $root "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
$data = Join-Path $root 'Data'

if (-not (Test-Path $exe)) {
    Write-Error "找不到 $exe，先 dotnet build。"
    exit 1
}

if (-not (Test-Path $PdfDir)) {
    Write-Error "找不到样本目录 $PdfDir（PDF TestCorpus应由用户放进这里）。"
    exit 1
}

$corpus = (Resolve-Path $PdfDir).Path
$pdfs = Get-ChildItem -Path $corpus -Filter *.pdf -File
if ($pdfs.Count -eq 0) {
    Write-Error "$corpus 里没有 PDF。"
    exit 1
}

Write-Host "样本目录 $corpus（$($pdfs.Count) 个 PDF）"
foreach ($p in $pdfs) {
    Write-Host ("  - {0}  [{1:N1} MB]" -f $p.Name, ($p.Length / 1MB))
}
Write-Host ''

$jsonIn    = Join-Path $data 'Temp\t62-probe.json'
$mdIn      = Join-Path $data 'Temp\t62-probe.md'
$shotsIn   = Join-Path $data 'Temp\t62-probe-shots'

$jsonOut   = Join-Path $root 'build\t62-probe-report.json'
$mdOut     = Join-Path $root 'build\t62-probe-report.md'
$shotsOut  = Join-Path $root 'build\t62-probe-shots'

Remove-Item $jsonIn -ErrorAction SilentlyContinue
Remove-Item $mdIn -ErrorAction SilentlyContinue

# 参数必须整体作为一个带引号的字符串传：路径与数据目录都含空格。
$argLine = '--t62-probe="' + $corpus + '" --datadir="' + $data + '"'

Write-Host "启动探针（最多 $TimeoutSeconds 秒）..."
$proc = Start-Process -FilePath $exe -ArgumentList $argLine -PassThru

if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
    $proc.Kill()
    Write-Error "探针超过 $TimeoutSeconds 秒未退出，已强制结束。"
    exit 1
}

Write-Host "探针进程 exit=$($proc.ExitCode)"
Write-Host ''

if (-not (Test-Path $jsonIn)) {
    Write-Error "报告未生成：$jsonIn（进程 exit=$($proc.ExitCode)，先看 Data\Logs 里的日志）"
    exit 1
}

$r = Get-Content $jsonIn -Raw -Encoding UTF8 | ConvertFrom-Json

# 归档：报告与截图都留在 build\ 下，供人眼复核。
Copy-Item $jsonIn $jsonOut -Force

if (Test-Path $mdIn) {
    Copy-Item $mdIn $mdOut -Force
}

if (Test-Path $shotsOut) {
    Remove-Item $shotsOut -Recurse -Force
}

if (Test-Path $shotsIn) {
    Copy-Item $shotsIn $shotsOut -Recurse -Force
}

Write-Host ("WebView2    {0}" -f $r.BrowserVersion)
Write-Host ("外壳主机    {0} / PDF 文件主机 {1}（{2}）" -f $r.ShellHost, $r.PdfFileHost, $r.PdfAccessKind)
Write-Host ''
Write-Host '九项判定：'

foreach ($f in $r.Findings) {
    $color = if ($f.Verdict -like '未得出结论*') { 'Red' } else { 'Green' }
    Write-Host ("  [{0}] {1}" -f $f.Id, $f.Verdict) -ForegroundColor $color
    Write-Host ("       分支：{0}" -f $f.Branch)
}

Write-Host ''
Write-Host ("截图 {0} 张 -> {1}" -f $r.Shots.Count, $shotsOut)
Write-Host ("报告 -> {0}" -f $mdOut)
Write-Host ''

$missing = @($r.Findings | Where-Object { $_.Verdict -like '未得出结论*' })

if ($r.Findings.Count -ne 9 -or $missing.Count -gt 0) {
    Write-Host ("有 {0} 项没跑出结论。" -f $missing.Count) -ForegroundColor Red
    exit 2
}

Write-Host '九项都跑出了结论（结论内容见报告；"某按钮阶段一置灰"也是合格结论）。' -ForegroundColor Green
exit 0
