<#
.SYNOPSIS
    T2-4 转换队列端到端冒烟（6 条完成判据）。

.DESCRIPTION
    用 -File 指定的一本真实 EPUB 造出 10 份样本，导入后一次排 10 本转换，然后验：
      a 排 10 本，依次执行，全部成功（产物真的落盘）
      b 库里残留的 Running 在下次启动时归位成排队并接着跑完
      c 磁盘满 -> 明确失败，且一条也不重试
      e [全部取消] 后 <= 10 秒所有外部进程消失
      f 失败/成功都能在「查看日志」里看到 Calibre 的完整输出
      另外还验：队列页 XAML 能构造、重复入队被拒、没有残留临时目录。

    样本怎么造的：把模板 EPUB 原样复制 10 份，每份在【文件末尾】追加一小段唯一尾巴。
    这样长度与尾部都变了（快速指纹必然不同），而 zip 的中央目录在尾巴之前，
    所以 ZipArchive 与 Calibre 都照常能读。直接复制 10 份会被判成同一本书。

    这条冒烟要真跑 10 次 Calibre 转换，大约 1-3 分钟。

    退出码：0 = 全部通过；2 = 有检查项失败；1 = 冒烟本身跑不起来。

.EXAMPLE
    powershell -File build\smoke-convert-queue.ps1 -File "TestCorpus\sample-epub-1.epub"
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
    $DataDir = Join-Path $repoRoot ("Data-Test\convert-queue-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $temporary = $true
}

$DataDir = [IO.Path]::GetFullPath($DataDir)
if (Test-Path -LiteralPath $DataDir) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}

# 样本：模板复制 10 份，每份在末尾追加唯一尾巴（改长度也改尾部，指纹必然不同）
$corpus = Join-Path ([IO.Path]::GetTempPath()) ("ets-queue-corpus-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $corpus | Out-Null

$lf = [string][char]10
$bytes = [IO.File]::ReadAllBytes($template)
for ($i = 1; $i -le 10; $i++) {
    $target = Join-Path $corpus ("book-{0:D2}.epub" -f $i)
    $trailer = [Text.Encoding]::ASCII.GetBytes($lf + "ETS-QUEUE-SMOKE-" + $i.ToString('D3') + $lf)
    $stream = New-Object System.IO.MemoryStream
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Write($trailer, 0, $trailer.Length)
    [IO.File]::WriteAllBytes($target, $stream.ToArray())
    $stream.Dispose()
}

Write-Host "模板：$template"
Write-Host "主程序：$exe"
Write-Host "数据目录：$DataDir"
Write-Host "样本目录：$corpus（10 份，各带唯一尾巴）"
Write-Host "正在真跑 10 次 Calibre 转换，请稍等（约 1-3 分钟）..."
Write-Host ''

# 参数必须整体作为「一个带引号的字符串」传，否则路径里的空格会被当成参数分隔符
$arguments = '--smoke-convert-queue="' + $corpus + '" --datadir="' + $DataDir + '"'

$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait

$reportPath = Join-Path $DataDir 'Temp\convert-queue-smoke.json'
if (-not (Test-Path -LiteralPath $reportPath)) {
    Write-Host "没有生成报告：$reportPath（退出码 $($process.ExitCode)）" -ForegroundColor Red
    if (Test-Path -LiteralPath (Join-Path $DataDir 'Logs')) {
        Write-Host '日志尾部：' -ForegroundColor Yellow
        Get-ChildItem -LiteralPath (Join-Path $DataDir 'Logs') -Filter '*.log' |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
            ForEach-Object { Get-Content -LiteralPath $_.FullName -Tail 30 -Encoding UTF8 }
    }

    try { Remove-Item -LiteralPath $corpus -Recurse -Force } catch { }
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

# 样本是临时的，无论成败都清掉
try { Remove-Item -LiteralPath $corpus -Recurse -Force } catch { }

# 失败时保留数据目录与日志供排查；成功且是临时目录就收干净。
if ($failed -eq 0 -and $temporary -and -not $KeepDataDir) {
    try { Remove-Item -LiteralPath $DataDir -Recurse -Force } catch { }
}
else {
    Write-Host ("数据目录保留在：{0}" -f $DataDir) -ForegroundColor Yellow
}

if ($failed -gt 0) { exit 2 }
exit 0
