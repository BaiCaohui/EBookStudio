# T6-2B「PDF 自绘引擎」验收驱动脚本（判据 ①~⑥）
#
# 用法：
#   powershell -File build\smoke-t62b.ps1
#   powershell -File build\smoke-t62b.ps1 -PdfDir '.\TestCorpus' -Configuration Release
#
# 它做四件事：
#   1) **用一个全新的 datadir**（默认 Data-Test\t62b-acceptance，先删再建）——
#      判据⑥ 要断言 Data\Cache\Pages 仍不存在，老 Data 目录是改动前建的、里面那个空目录还在，
#      不清空就验不出东西；
#   2) 起 EBookStudio.exe --smoke-t62b="<样本目录>" --datadir="<新 Data>";
#   3) 把 <Data>\Temp\t62b-smoke.json 收成 build\t62b-acceptance-report.json 并生成 .md，
#      截图从 <Data>\Temp\t62b-shots 收到 build\t62b-shots\；
#   4) 打印每一项判定。
#
# 退出码：0 = 可验收的判据全绿（另有两项"未验收/已转交"见报告）；2 = 有判据未通过；1 = 跑不起来。
# 注意：判据④（全库搜索命中）与判据⑤（书签笔记位置）已由用户决定转交 T6-4 / T6-5，
#      报告里记 **未验收**（ok=false），不假装通过——所以 EXE 自身的退出码必然是 2。

param(
    [string]$PdfDir = '.\TestCorpus',
    [string]$Configuration = 'Debug',
    [string]$DataDir = 'Data-Test\t62b-acceptance',
    [int]$TimeoutSeconds = 2400
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$exe  = Join-Path $root "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
$data = Join-Path $root $DataDir

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

# 全新 datadir（判据⑥ 的前提）
if (Test-Path $data) {
    Write-Host "清空上次的数据目录 $data"
    Remove-Item -Path $data -Recurse -Force
}

New-Item -ItemType Directory -Path $data -Force | Out-Null
Write-Host "本次数据目录（全新）$data"

$jsonIn  = Join-Path $data 'Temp\t62b-smoke.json'
$jsonOut = Join-Path $root 'build\t62b-acceptance-report.json'
$mdOut   = Join-Path $root 'build\t62b-acceptance-report.md'
$shotsIn = Join-Path $data 'Temp\t62b-shots'
$shotsOut = Join-Path $root 'build\t62b-shots'

if (Test-Path $shotsOut) {
    Remove-Item -Path $shotsOut -Recurse -Force
}

Remove-Item $jsonIn -ErrorAction SilentlyContinue

# 参数必须整体作为一个带引号的字符串传：路径与数据目录都含空格。
$argLine = '--smoke-t62b="' + $corpus + '" --datadir="' + $data + '"'

Write-Host "启动验收（最多 $TimeoutSeconds 秒：四本样本都要真开窗口、真翻页、真截图）..."
$proc = Start-Process -FilePath $exe -ArgumentList $argLine -PassThru

if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
    $proc.Kill()
    Write-Error "验收超过 $TimeoutSeconds 秒未退出，已强制结束。"
    exit 1
}

$proc.WaitForExit()
$exitCode = $proc.ExitCode

Write-Host "验收进程 exit=$exitCode"
Write-Host ''

if (-not (Test-Path $jsonIn)) {
    Write-Error "报告未生成：$jsonIn（进程 exit=$exitCode，先看 $data\Logs 里的日志）"
    exit 1
}

Copy-Item $jsonIn $jsonOut -Force

$r = Get-Content $jsonIn -Raw -Encoding UTF8 | ConvertFrom-Json

# 截图收集
if (Test-Path $shotsIn) {
    New-Item -ItemType Directory -Path $shotsOut -Force | Out-Null
    Copy-Item (Join-Path $shotsIn '*.png') $shotsOut -Force -ErrorAction SilentlyContinue
}

$shots = @(Get-ChildItem -Path $shotsOut -Filter *.png -File -ErrorAction SilentlyContinue | Sort-Object Name)

# 被测 revision：并行改代码时，"这份报告到底测的是哪一版"必须能查
function Get-Stamp([string]$path) {
    if (-not (Test-Path $path)) { return "$([System.IO.Path]::GetFileName($path)) = 缺失" }
    $item = Get-Item $path
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.Substring(0, 12)
    return ("{0} = {1} sha256:{2}" -f $item.Name, $item.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'), $hash)
}

$stamps = @(
    (Get-Stamp (Join-Path $root 'src\EbookToolSuite.Reader\Assets\Reader\reader.js')),
    (Get-Stamp (Join-Path $root 'src\EbookToolSuite.App\Views\ReaderWindow.xaml.cs')),
    (Get-Stamp (Join-Path $root 'src\EbookToolSuite.App\App.xaml.cs')),
    (Get-Stamp (Join-Path $root 'src\EbookToolSuite.App\Smoke\T62BSmokeTest.cs')),
    (Get-Stamp $exe),
    (Get-Stamp (Join-Path $root 'src\EbookToolSuite.App\Smoke\T62SmokeTest.cs'))
)

$readerCacheDir = Join-Path $data 'Cache\Reader'
$readerCacheHashes = @(Get-ChildItem -Path $readerCacheDir -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)

Write-Host '逐项判定：'
foreach ($c in $r.Checks) {
    $color = if ($c.Ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}" -f $(if ($c.Ok) { 'OK  ' } else { 'FAIL' }), $c.Name) -ForegroundColor $color
    Write-Host ("        {0}" -f $c.Detail)
}

$passed   = @($r.Checks | Where-Object { $_.Ok }).Count
$total    = @($r.Checks).Count
$deferred = @($r.Checks | Where-Object { $_.Name -like '*deferred*' })
$blocking = @($r.Checks | Where-Object { -not $_.Ok -and $_.Name -notlike '*deferred*' })

Write-Host ''
Write-Host ("通过 {0}/{1}，用时 {2:N0} ms；其中未验收（已转交 T6-4/T6-5）{3} 项" -f `
    $passed, $total, $r.DurationMs, $deferred.Count)
Write-Host ("截图 {0} 张 -> {1}" -f $shots.Count, $shotsOut)

# 生成 markdown 报告（样本 + 逐项结论 + 数字 + 已知限制）
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('# T6-2B「PDF 自绘引擎」验收报告')
[void]$sb.AppendLine()
[void]$sb.AppendLine("> 生成时间 $($r.StartedAt) → $($r.FinishedAt)（用时 $($r.DurationMs) ms）")
[void]$sb.AppendLine("> 数据目录 ``$($r.DataRoot)``（本次是**全新**目录：判据⑥ 要求 ``Cache\Pages`` 不存在）")
[void]$sb.AppendLine("> 入口：``powershell -File build\smoke-t62b.ps1``；机器可读版见 ``build\t62b-acceptance-report.json``")
[void]$sb.AppendLine("> EXE 退出码：$exitCode（$($passed)/$($total) 通过）")
[void]$sb.AppendLine('> 被测 revision（并行改代码时用它对账）：')
foreach ($stamp in $stamps) {
    [void]$sb.AppendLine(("- " + $stamp))
}
[void]$sb.AppendLine(("- 本次读者缓存目录 hash = " + $(if ($readerCacheHashes.Count -gt 0) { $readerCacheHashes -join ', ' } else { '(无)' })))
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 样本')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| 文件 | 大小 |')
[void]$sb.AppendLine('|---|---:|')
foreach ($p in $pdfs) {
    [void]$sb.AppendLine(("| {0} | {1:N1} MB |" -f $p.Name, ($p.Length / 1MB)))
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("## 逐项结果（$passed/$total 通过）")
[void]$sb.AppendLine()
[void]$sb.AppendLine('| 检查 | 结果 | 说明 |')
[void]$sb.AppendLine('|---|---|---|')
foreach ($c in $r.Checks) {
    $mark = if ($c.Ok) { '通过' } else { '**未通过**' }
    $detail = $c.Detail -replace '\|', '/'
    [void]$sb.AppendLine(("| {0} | {1} | {2} |" -f $c.Name, $mark, $detail))
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 未验收（已由用户决定转交，本次不报 OK）')
[void]$sb.AppendLine()
foreach ($c in $deferred) {
    [void]$sb.AppendLine("- **$($c.Name)**：$($c.Detail)")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 判据②③的原始数字')
[void]$sb.AppendLine()
foreach ($c in $r.Checks) {
    if ($c.Name -like 'J2-*' -or $c.Name -like 'J3-*') {
        [void]$sb.AppendLine("- **$($c.Name)**：$($c.Detail)")
    }
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 截图（判据①与附加项的画面存证）')
[void]$sb.AppendLine()
foreach ($s in $shots) {
    [void]$sb.AppendLine(("- ``{0}``（{1:N0} B）" -f $s.Name, $s.Length))
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 已知限制（必须随报告一起看）')
[void]$sb.AppendLine()
[void]$sb.AppendLine('1. **判据④⑤未验收**：全库搜索命中（T6-4）与书签笔记位置（T6-5）已由用户决定转交，')
[void]$sb.AppendLine('   本报告把它们记成 ok=false 并在上面单列，**不代表失败**，也不代表通过。')
[void]$sb.AppendLine('2. **内存只量宿主进程**（``Process.WorkingSet64``）：WebView2 的渲染进程是独立进程，')
[void]$sb.AppendLine('   判据③ 的 500 MB 是宿主进程的口径，与 T6-2 保持一致。')
[void]$sb.AppendLine('3. **判据②/③的时间受系统文件缓存影响**：同一份文件第二次跑会明显更快，')
[void]$sb.AppendLine('   所以判据②同时报 min/avg/max/p95 与"预取命中页数"，不只看平均值。')
[void]$sb.AppendLine('4. **判据⑥ 只在全新 datadir 下有意义**：老 ``Data`` 目录里的 ``Cache\Pages`` 是改动前预建的，')
[void]$sb.AppendLine('   所以本脚本每次都换一个全新的测试数据目录。')
[void]$sb.AppendLine()

[System.IO.File]::WriteAllText($mdOut, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host ("报告 -> {0}" -f $mdOut)
Write-Host ("机器可读 -> {0}" -f $jsonOut)
Write-Host ''

if ($blocking.Count -eq 0) {
    Write-Host ("可验收的判据全绿；另有 {0} 项未验收（已转交 T6-4/T6-5，见报告）。" -f $deferred.Count) -ForegroundColor Green
    exit 0
}

Write-Host ("有 {0} 项判据未通过。" -f $blocking.Count) -ForegroundColor Red
exit 2
