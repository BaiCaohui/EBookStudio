<#
.SYNOPSIS
    T6-1「MOBI / AZW3 惰性转 EPUB 缓存」端到端验收（真 Calibre）。

.DESCRIPTION
    1) 用真 Calibre 造样本：约 5 MB 的 MOBI 两本（内容不同，用来验"源被替换后缓存失效"）、
       一本合法但会被换坏的 MOBI、一本 AZW3。
    2) 跑 App 的 --smoke-t61 模式，在真阅读服务 + 真转换队列上验 6 条完成判据：
       1 首次打开 MOBI 自动转换+缓存+显示，<= 15 秒
       2 第二次打开 <= 2 秒（缓存命中）
       3 源 MOBI 被替换后缓存自动失效
       4 转换失败有明确提示 + 「用系统默认程序打开」按钮（真构造阅读窗口并真点一次）
       5 打开操作插队到转换队列前面
       6 缓存上限清理跳过正在读的
    3) 输出 build\t61-acceptance-report.md 与 .json。

    退出码：0 = 全绿；2 = 有检查项失败；1 = 跑不起来（缺 Calibre / 缺主程序 / 没出报告）。

.EXAMPLE
    powershell -File build\smoke-t61.ps1
#>
[CmdletBinding()]
param(
    [string]$FixtureDir,

    [string]$DataDir,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # 直接指定被测的可执行文件（发布产物验收用；不传就用 src 下的构建产物）
    [string]$ExePath,

    [switch]$RebuildFixtures,

    [switch]$KeepDataDir,

    [switch]$FixturesOnly
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

$convert = Join-Path $repoRoot 'Tools\Calibre Portable\Calibre\ebook-convert.exe'
if (-not (Test-Path -LiteralPath $convert)) {
    Write-Host "找不到 Calibre：$convert" -ForegroundColor Red
    exit 1
}

if ([string]::IsNullOrWhiteSpace($FixtureDir)) {
    $FixtureDir = Join-Path $repoRoot 'Data-Test\t61-fixtures'
}

$FixtureDir = [IO.Path]::GetFullPath($FixtureDir)
if (-not (Test-Path -LiteralPath $FixtureDir)) {
    New-Item -ItemType Directory -Force -Path $FixtureDir | Out-Null
}

# 数据目录刻意取短：缓存键里带指纹 + mtime + size，太长会顶到 Windows 路径上限
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot 'Data-Test\t61'
}

$DataDir = [IO.Path]::GetFullPath($DataDir)
if (Test-Path -LiteralPath $DataDir) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem, PresentationCore, WindowsBase

function Invoke-Native([string]$FilePath, [string[]]$Arguments, [string]$LogName) {
    $log = Join-Path $FixtureDir ($LogName + '.log')
    $p = Start-Process -FilePath $FilePath -ArgumentList $Arguments -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
    return $p.ExitCode
}

function New-NoisePng([int]$Width, [int]$Height, [int]$Seed, [string]$Path) {
    $stride = $Width * 3
    $pixels = New-Object byte[] ($stride * $Height)
    $rand = New-Object Random($Seed)
    $chunk = New-Object byte[] 65536
    for ($i = 0; $i -lt $pixels.Length; $i += $chunk.Length) {
        $n = [Math]::Min($chunk.Length, $pixels.Length - $i)
        $rand.NextBytes($chunk)
        [Array]::Copy($chunk, 0, $pixels, $i, $n)
    }

    $source = [System.Windows.Media.Imaging.BitmapSource]::Create(
        $Width, $Height, 96, 96, [System.Windows.Media.PixelFormats]::Bgr24, $null, $pixels, $stride)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($source))
    $stream = [IO.File]::Create($Path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

# 造一本"被正文引用的大图"的 EPUB：未引用的附件会被 Calibre 裁掉，所以必须嵌进章节里。
function New-BigImageEpub([string]$Template, [string]$Destination, [string[]]$Images) {
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }

    $items = @()
    for ($i = 0; $i -lt $Images.Count; $i++) {
        $items += [pscustomobject]@{
            Entry = ('OEBPS/images/pad{0}.png' -f ($i + 1))
            Rel   = ('images/pad{0}.png' -f ($i + 1))
            Bytes = [IO.File]::ReadAllBytes($Images[$i])
        }
    }

    $manifestAdd = ''
    $imgTags = ''
    for ($i = 0; $i -lt $items.Count; $i++) {
        $manifestAdd += "`n    " + ('<item id="padimg{0}" href="{1}" media-type="image/png"/>' -f ($i + 1), $items[$i].Rel)
        $imgTags += ('<p><img src="{0}" alt="pad{1}"/></p>' -f $items[$i].Rel, ($i + 1))
    }

    $input = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Template).ProviderPath)
    $output = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $input.Entries) {
            if ($entry.FullName -eq 'OEBPS/content.opf') {
                $reader = New-Object IO.StreamReader($entry.Open())
                $opf = $reader.ReadToEnd()
                $reader.Dispose()
                $opf = $opf.Replace(
                    '<item id="ch5" href="ch5.xhtml" media-type="application/xhtml+xml"/>',
                    '<item id="ch5" href="ch5.xhtml" media-type="application/xhtml+xml"/>' + $manifestAdd)
                $new = $output.CreateEntry($entry.FullName, [IO.Compression.CompressionLevel]::Optimal)
                $writer = New-Object IO.StreamWriter($new.Open(), (New-Object Text.UTF8Encoding($false)))
                $writer.Write($opf)
                $writer.Dispose()
                continue
            }

            if ($entry.FullName -eq 'OEBPS/ch1.xhtml') {
                $reader = New-Object IO.StreamReader($entry.Open())
                $chapter = $reader.ReadToEnd()
                $reader.Dispose()
                $chapter = $chapter.Replace(
                    '<p>这是第 1 章的正文内容。</p>',
                    '<p>这是第 1 章的正文内容。</p>' + $imgTags)
                $new = $output.CreateEntry($entry.FullName, [IO.Compression.CompressionLevel]::Optimal)
                $writer = New-Object IO.StreamWriter($new.Open(), (New-Object Text.UTF8Encoding($false)))
                $writer.Write($chapter)
                $writer.Dispose()
                continue
            }

            # mimetype 必须第一条且不压缩，所以这里按原顺序原样搬。
            $level = if ($entry.FullName -eq 'mimetype') {
                [IO.Compression.CompressionLevel]::NoCompression
            }
            else {
                [IO.Compression.CompressionLevel]::Optimal
            }

            $new = $output.CreateEntry($entry.FullName, $level)
            $s = $entry.Open()
            $d = $new.Open()
            $s.CopyTo($d)
            $d.Dispose()
            $s.Dispose()
        }

        foreach ($item in $items) {
            $new = $output.CreateEntry($item.Entry, [IO.Compression.CompressionLevel]::NoCompression)
            $d = $new.Open()
            try { $d.Write($item.Bytes, 0, $item.Bytes.Length) } finally { $d.Dispose() }
        }
    }
    finally {
        $output.Dispose()
        $input.Dispose()
    }
}

function New-FixtureEpubTo([string]$Source, [string]$Destination, [string]$LogName) {
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    $code = Invoke-Native $convert @("`"$Source`"", "`"$Destination`"") $LogName
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $Destination)) {
        Write-Host "Calibre 造样本失败：$LogName（exit $code）" -ForegroundColor Red
        Get-Content -LiteralPath (Join-Path $FixtureDir ($LogName + '.log.err')) -Encoding UTF8 |
            Select-Object -Last 5 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkRed }
        exit 1
    }
}

$templateEpub = Join-Path $repoRoot 'TestCorpus\t56-valid-5ch.epub'
if (-not (Test-Path -LiteralPath $templateEpub)) {
    Write-Host "找不到 EPUB 模板：$templateEpub" -ForegroundColor Red
    exit 1
}

$mobiA = Join-Path $FixtureDir 't61-a.mobi'
$mobiB = Join-Path $FixtureDir 't61-b.mobi'
$badMobi = Join-Path $FixtureDir 't61-bad.mobi'
$azw3 = Join-Path $FixtureDir 't61.azw3'

$needBuild = $RebuildFixtures -or -not (Test-Path -LiteralPath $mobiA) -or -not (Test-Path -LiteralPath $mobiB) `
    -or -not (Test-Path -LiteralPath $badMobi) -or -not (Test-Path -LiteralPath $azw3)

if ($needBuild) {
    Write-Host '--- 用真 Calibre 造样本 ---'

    # A：3 张 1600x1200 随机噪声图 -> EPUB 约 17 MB -> MOBI 约 4.8 MB（判据 1 的"5 MB 的书"）
    $pngA = @(1..3 | ForEach-Object { Join-Path $FixtureDir ('pad-a{0}.png' -f $_) })
    for ($i = 0; $i -lt $pngA.Count; $i++) { New-NoisePng 1600 1200 (101 + $i * 7) $pngA[$i] }
    New-BigImageEpub $templateEpub (Join-Path $FixtureDir 'big-a.epub') $pngA
    New-FixtureEpubTo (Join-Path $FixtureDir 'big-a.epub') $mobiA 'build-mobi-a'

    # B：2 张（比 A 小，保证"替换源文件"时大小也变了）
    $pngB = @(1..2 | ForEach-Object { Join-Path $FixtureDir ('pad-b{0}.png' -f $_) })
    for ($i = 0; $i -lt $pngB.Count; $i++) { New-NoisePng 1600 1200 (202 + $i * 7) $pngB[$i] }
    New-BigImageEpub $templateEpub (Join-Path $FixtureDir 'big-b.epub') $pngB
    New-FixtureEpubTo (Join-Path $FixtureDir 'big-b.epub') $mobiB 'build-mobi-b'

    # 坏书样本：先是一本合法 MOBI（能导入），冒烟里再把库内文件换成随机字节。
    New-FixtureEpubTo $templateEpub $badMobi 'build-bad-mobi'

    New-FixtureEpubTo $templateEpub $azw3 'build-azw3'
}

foreach ($f in @($mobiA, $mobiB, $badMobi, $azw3)) {
    if (-not (Test-Path -LiteralPath $f)) {
        Write-Host "样本缺失：$f" -ForegroundColor Red
        exit 1
    }
}

Write-Host '--- 样本 ---'
foreach ($f in @($mobiA, $mobiB, $badMobi, $azw3)) {
    Write-Host ("    {0,-14} {1,12:N0} 字节" -f (Split-Path -Leaf $f), (Get-Item -LiteralPath $f).Length)
}

if ($FixturesOnly) {
    Write-Host '（-FixturesOnly：只造样本，退出；跑冒烟请不加该开关）'
    exit 0
}

Write-Host ''
Write-Host '--- 跑 --smoke-t61（真 Calibre / 真阅读服务 / 真转换队列）---'
Write-Host "数据目录：$DataDir"

$arguments = '--smoke-t61="' + $FixtureDir + '" --datadir="' + $DataDir + '"'
$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait

$reportPath = Join-Path $DataDir 'Temp\t61-smoke.json'
if (-not (Test-Path -LiteralPath $reportPath)) {
    Write-Host "没有生成报告：$reportPath（退出码 $($process.ExitCode)）" -ForegroundColor Red
    exit 1
}

$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Host ''
Write-Host ("冒烟用时 {0:N0} ms" -f $report.DurationMs)
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

function Get-Check([string]$Name) {
    return ($report.Checks | Where-Object { $_.Name -eq $Name } | Select-Object -First 1)
}

function Test-Named([string[]]$Names) {
    $miss = @()
    $bad = @()
    foreach ($n in $Names) {
        $c = Get-Check $n
        if ($null -eq $c) { $miss += $n }
        elseif (-not $c.Ok) { $bad += $n }
    }

    return [pscustomobject]@{ Ok = ($miss.Count -eq 0 -and $bad.Count -eq 0); Detail = ("缺 {0} 项 / 失败 {1} 项" -f $miss.Count, $bad.Count) }
}

# 6 条完成判据（计划文档原文）+ 3 条设计约束
$j1 = Test-Named @('J1-first-open-converts', 'J1-converted-cache-layout', 'J1-under-15s')
$j2 = Test-Named @('J2-second-open-cache-hit', 'J2-chapter-materializes')
$j3 = Test-Named @('J3-source-replaced-invalidates')
$j4 = Test-Named @('J4-failure-explicit-message', 'J4-ui-overlay-and-external-button', 'J4-button-hidden-without-source')
$j5 = Test-Named @('J5-open-jumps-queue')
$j6 = Test-Named @('J6-evicts-oldest-skips-in-use', 'J6-release-then-evictable')
$design = Test-Named @('design-no-epub-row-registered')

$first = Get-Check 'J1-first-open-converts'
$second = Get-Check 'J2-second-open-cache-hit'
$jump = Get-Check 'J5-open-jumps-queue'
$limit = Get-Check 'J6-evicts-oldest-skips-in-use'

$steps = New-Object System.Collections.Generic.List[object]
function Add-Step([int]$No, [string]$Title, [bool]$Ok, [string]$Evidence) {
    $steps.Add([pscustomobject]@{ No = $No; Title = $Title; Ok = $Ok; Evidence = $Evidence })
}

Add-Step 1 '打开一本 MOBI，自动转换 + 缓存 + 显示，总耗时 <= 15 秒（一本 5 MB 的书）。' $j1.Ok $first.Detail
Add-Step 2 '第二次打开同一本 MOBI，<= 2 秒（缓存命中）。' $j2.Ok $second.Detail
Add-Step 3 '源 MOBI 被替换后，缓存自动失效。' $j3.Ok (Get-Check 'J3-source-replaced-invalidates').Detail
Add-Step 4 '转换失败时有明确提示 + "用外部阅读器打开"按钮。' $j4.Ok (Get-Check 'J4-ui-overlay-and-external-button').Detail
Add-Step 5 '打开操作会插队到转换队列前面（若队列有任务）。' $j5.Ok $jump.Detail
Add-Step 6 '缓存大小受上限管理，清理时跳过正在读的。' $j6.Ok $limit.Detail

$allOk = (($steps | Where-Object { -not $_.Ok }).Count -eq 0) -and $report.Passed

$md = New-Object System.Collections.Generic.List[string]
$md.Add('# T6-1 MOBI / AZW3 阅读（惰性转 EPUB 缓存）验收报告')
$md.Add('')
$md.Add(("- 生成时间：{0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')))
$md.Add(("- 样本目录：{0}" -f $FixtureDir))
$md.Add(("- 数据目录：{0}" -f $DataDir))
$md.Add(("- 5 MB 样本：{0}（{1:N0} 字节）" -f (Split-Path -Leaf $mobiA), (Get-Item -LiteralPath $mobiA).Length))
$md.Add(("- 结论：{0}" -f $(if ($allOk) { '6 条完成判据全部通过' } else { '有判据未通过' })))
$md.Add('')
$md.Add('## 6 条完成判据与结果（标准原样不改）')
$md.Add('')
$md.Add('| # | 完成判据 | 结果 | 证据（实测） |')
$md.Add('| --- | --- | --- | --- |')
foreach ($s in $steps) {
    $md.Add(("| {0} | {1} | {2} | {3} |" -f $s.No, $s.Title, $(if ($s.Ok) { 'PASS' } else { 'FAIL' }), $s.Evidence))
}
$md.Add('')
$md.Add('## 设计约束')
$md.Add('')
$md.Add(("- 转出的 EPUB 不进 book_formats：{0}（{1}）" -f $(if ($design.Ok) { 'PASS' } else { 'FAIL' }), (Get-Check 'design-no-epub-row-registered').Detail))
$md.Add(("- AZW3 用通用参数转 EPUB（无 --mobi-file-type）：{0}" -f $(if ($j5.Ok) { 'PASS（见判据 5 证据）' } else { 'FAIL' })))
$md.Add('')
$md.Add('## 原始检查项')
$md.Add('')
$md.Add('| 检查项 | 结果 | 明细 |')
$md.Add('| --- | --- | --- |')
foreach ($check in $report.Checks) {
    $md.Add(("| {0} | {1} | {2} |" -f $check.Name, $(if ($check.Ok) { 'OK' } else { 'FAIL' }), $check.Detail))
}
$md.Add('')
$md.Add('## 人工复核项')
$md.Add('- 在真机上打开一本 MOBI，确认转换期间的等待提示观感。')
$md.Add('- 双击「用系统默认程序打开」后由系统选择外部阅读器（冒烟里只验到"交给系统"这一步，不真的拉起程序）。')

$reportMd = Join-Path $repoRoot 'build\t61-acceptance-report.md'
Set-Content -LiteralPath $reportMd -Value ($md -join "`r`n") -Encoding UTF8

$jsonOut = Join-Path $repoRoot 'build\t61-acceptance-report.json'
[pscustomobject]@{
    GeneratedAt   = (Get-Date).ToString('o')
    AllOk         = $allOk
    Steps         = $steps
    Checks        = $report.Checks
    SmokeExitCode = $process.ExitCode
    FixtureDir    = $FixtureDir
    DataDir       = $DataDir
    MobiABytes    = (Get-Item -LiteralPath $mobiA).Length
    MobiBBytes    = (Get-Item -LiteralPath $mobiB).Length
    Report        = $reportMd
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonOut -Encoding UTF8

Write-Host ''
Write-Host '==== T6-1 六条完成判据 ===='
foreach ($s in $steps) {
    $color = if ($s.Ok) { 'Green' } else { 'Red' }
    $text = if ($s.Ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] 判据{1} {2}" -f $text, $s.No, $s.Title) -ForegroundColor $color
    Write-Host ("        {0}" -f $s.Evidence) -ForegroundColor Gray
}

Write-Host ''
Write-Host ("报告：{0}" -f $reportMd)
Write-Host ("数据：{0}" -f $DataDir)

if ($KeepDataDir) { Write-Host '（已保留数据目录）' }

if (-not $allOk) { exit 2 }
exit 0