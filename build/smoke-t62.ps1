# T6-2 PDF 阅读验收驱动脚本（判据 1~7）
#
# 用法：
#   powershell -File build\smoke-t62.ps1
#   powershell -File build\smoke-t62.ps1 -PdfDir '.\TestCorpus' -Configuration Release
#
# 它做三件事：
#   1) 起 EBookStudio.exe --smoke-t62="<样本目录>" --datadir="<Data>"
#   2) 把 Data\Temp\t62-smoke.json 收成 build\t62-acceptance-report.json 并生成 .md
#   3) 打印每一项判定
#
# 退出码：0 = 判据全绿；2 = 有失败；1 = 跑不起来。
# 注意：判据 4 的"100 MB 首屏 ≤5 s"是**已知限制**（用户 2026-10-13 接受），
#      脚本里那一条的期望是"确实超过了 5 秒且原因已查清"，不是"没超"。

param(
    [string]$PdfDir = '.\TestCorpus',
    [string]$Configuration = 'Debug',
    [int]$TimeoutSeconds = 1200
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

$jsonIn  = Join-Path $data 'Temp\t62-smoke.json'
$jsonOut = Join-Path $root 'build\t62-acceptance-report.json'
$mdOut   = Join-Path $root 'build\t62-acceptance-report.md'

Remove-Item $jsonIn -ErrorAction SilentlyContinue

# 参数必须整体作为一个带引号的字符串传：路径与数据目录都含空格。
$argLine = '--smoke-t62="' + $corpus + '" --datadir="' + $data + '"'

Write-Host "启动验收（最多 $TimeoutSeconds 秒；大文件那本会真的等首屏）..."
$proc = Start-Process -FilePath $exe -ArgumentList $argLine -PassThru

if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
    $proc.Kill()
    Write-Error "验收超过 $TimeoutSeconds 秒未退出，已强制结束。"
    exit 1
}

Write-Host "验收进程 exit=$($proc.ExitCode)"
Write-Host ''

if (-not (Test-Path $jsonIn)) {
    Write-Error "报告未生成：$jsonIn（进程 exit=$($proc.ExitCode)，先看 Data\Logs 里的日志）"
    exit 1
}

Copy-Item $jsonIn $jsonOut -Force

$r = Get-Content $jsonIn -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Host '逐项判定：'
foreach ($c in $r.Checks) {
    $color = if ($c.Ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}" -f $(if ($c.Ok) { 'OK  ' } else { 'FAIL' }), $c.Name) -ForegroundColor $color
    Write-Host ("        {0}" -f $c.Detail)
}

$passed = @($r.Checks | Where-Object { $_.Ok }).Count
$total  = @($r.Checks).Count

Write-Host ''
Write-Host ("通过 {0}/{1}，用时 {2:N0} ms" -f $passed, $total, $r.DurationMs)

# 生成 markdown 报告（数据集 + 逐项结论）
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('# T6-2「PDF 阅读」验收报告')
[void]$sb.AppendLine()
[void]$sb.AppendLine("> 生成时间 $($r.StartedAt) → $($r.FinishedAt)（用时 $($r.DurationMs) ms）")
[void]$sb.AppendLine("> 数据目录 ``$($r.DataRoot)``")
[void]$sb.AppendLine("> 入口：``powershell -File build\smoke-t62.ps1``；机器可读版见 ``build\t62-acceptance-report.json``")
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
    $detail = ($c.Detail -replace '\|', '\|')
    [void]$sb.AppendLine(("| {0} | {1} | {2} |" -f $c.Name, $mark, $detail))
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 已知限制（必须随报告一起看）')
[void]$sb.AppendLine()
[void]$sb.AppendLine('1. **判据 4「100 MB 扫描件首屏 ≤5 s」不达标**（用户 2026-10-13 接受为已知限制）：')
[void]$sb.AppendLine('   114.8 MB 实测约 8.8 s；探针实测同一份文件走虚拟主机的**纯传输只要 128 ms（≈918 MB/s）**，')
[void]$sb.AppendLine('   瓶颈在 PDFium 解析非线性化 PDF。Range 请求与本地 HTTP 服务都救不了这个数。')
[void]$sb.AppendLine('2. **权限受限的 PDF 会多一条文档级提示条**（"此文件具有受限权限…查看权限 ×"）：')
[void]$sb.AppendLine('   那是渲染器行为，`#toolbar=0` 与 `HiddenPdfToolbarItems` 都关不掉；普通 PDF 顶部是干净的。')
[void]$sb.AppendLine('3. **"200 页文字版首屏 ≤1.5 s" 用中档那本（36.8 MB 插图本）近似**，口径不同，不得当达标。')
[void]$sb.AppendLine('4. **PDF 的加密角标未落库**：`book_formats` 没有可放这个标记的列，需要一次 schema 迁移；')
[void]$sb.AppendLine('   当前只在导入日志里记 WRN，角标待补。')
[void]$sb.AppendLine('5. **`Data/Cache/Pages` 目录存在但为空**：它是 `AppPaths.EnumerateRequiredDirectories()`')
[void]$sb.AppendLine('   在启动时预建的（为将来的 P1 页缓存预留），判据 5 的实质"我们不自建页缓存"按目录内容为空验收。')
[void]$sb.AppendLine('6. **PDF 文本可选中：以截图为准**：键盘注入到不了插件（CDP 与真实按键都试过，剪贴板 0 字），')
[void]$sb.AppendLine('   改成鼠标拖选后截图 —— 见 `build\t62-text-selection.png`（选中态在画面上看得见）。')
[void]$sb.AppendLine('   对照证据：pdfium 实测这本样本前 10 页有 **5080 字**文本层，渲染路径是 Chromium 内建 PDF 查看器。')
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 判据 4 的完整数字（分开报）')
[void]$sb.AppendLine()
foreach ($c in $r.Checks) {
    if ($c.Name -like 'J4-*') {
        [void]$sb.AppendLine("- **$($c.Name)**：$($c.Detail)")
    }
}
[void]$sb.AppendLine()

[System.IO.File]::WriteAllText($mdOut, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ("报告 -> {0}" -f $mdOut)
Write-Host ''

if ($passed -eq $total) {
    Write-Host '判据全绿（判据 4 的已知限制见报告）。' -ForegroundColor Green
    exit 0
}

Write-Host ("有 $($total - $passed) 项未通过。") -ForegroundColor Red
exit 2
