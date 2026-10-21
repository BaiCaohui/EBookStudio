<#
.SYNOPSIS
    阶段 3 验收（T3-7）：改元数据 + 换封面 -> 保存 -> 独立校验 -> 独立读元数据 -> 逐条目 diff。

.DESCRIPTION
    第 1-3 步（导入 / 改书名+换封面 / 保存）复用 smoke-edit.ps1 跑完整链路，
    然后用**独立于被测程序**的手段再验一遍：
      4) 真 EpubCheck 单独跑一遍     -> nFatal = 0 且 nError = 0
      5) 真 Calibre ebook-meta 读    -> 书名是改后的；--get-cover 能导出封面
      6) 逐条目 SHA256 对比          -> 只有 OPF 与封面这两个条目变了
    第 7 步（改坏 -> 被拦住）与第 8 步（从备份还原）由 smoke-edit 的 M/J/K/L/O 场景覆盖
    （门禁按 EpubCheck 的严重级别判断，不关心是哪条规则报的）。

    退出码：0 = 全部通过；2 = 有检查项失败；1 = 脚本本身跑不起来。

.EXAMPLE
    powershell -File build\smoke-accept.ps1 -File "E:\books\sample.epub"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$File,

    [string]$DataDir,

    [string]$ExePath,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [int]$TimeoutSeconds = 900,

    [switch]$KeepDataDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$expectedTitle = '冒烟改名（编辑入口）'
$expectedAuthor = '验收作者（冒烟）'
$expectedSeries = '验收系列'
$checks = New-Object System.Collections.ArrayList

function Add-Check([string]$name, [bool]$ok, [string]$detail) {
    [void]$checks.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    $mark = if ($ok) { 'OK  ' } else { 'FAIL' }
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("[{0}]   {1,-34} {2}" -f $mark, $name, $detail) -ForegroundColor $color
}

function Get-OpfText([string]$path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)

    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -match '\.opf$' } | Select-Object -First 1
        if ($null -eq $entry) { return '' }

        $reader = New-Object System.IO.StreamReader($entry.Open(), [System.Text.Encoding]::UTF8)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
}

function Get-EntryHashes([string]$path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $map = @{}
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)

    try {
        foreach ($entry in $zip.Entries) {
            $stream = $entry.Open()
            try {
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try { $hash = $sha.ComputeHash($stream) } finally { $sha.Dispose() }
            }
            finally { $stream.Dispose() }

            $map[$entry.FullName] = [BitConverter]::ToString($hash).Replace('-', '')
        }
    }
    finally { $zip.Dispose() }

    return $map
}

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $exe = Join-Path $repoRoot "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
}
else {
    $exe = (Resolve-Path -LiteralPath $ExePath).ProviderPath
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "找不到主程序：$exe（先 dotnet build -c $Configuration）" -ForegroundColor Red
    exit 1
}

$source = (Resolve-Path -LiteralPath $File).ProviderPath
$temporary = $false

if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot ("Data-Test\accept-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $temporary = $true
}

$DataDir = [IO.Path]::GetFullPath($DataDir)

Write-Host ""
Write-Host "===== 阶段 3 验收（T3-7）=====" -ForegroundColor Cyan
Write-Host "源文件  ：$source"
Write-Host "数据目录：$DataDir"
Write-Host ""

# ---------- 第 1-3 步 + 第 7-8 步：交给 smoke-edit ----------
$editScript = Join-Path $PSScriptRoot 'smoke-edit.ps1'
$editArgs = @{
    File           = $source
    DataDir        = $DataDir
    ExePath        = $exe
    TimeoutSeconds = $TimeoutSeconds
    KeepDataDir    = $true
}

& $editScript @editArgs
$editCode = $LASTEXITCODE
Add-Check 'edit-smoke-全链路' ($editCode -eq 0) "smoke-edit.ps1 退出码=$editCode（0 = 该冒烟各项全过）"

if ($editCode -ne 0) {
    Write-Host "编辑冒烟没过，后面的独立校验没有意义。" -ForegroundColor Red
    exit 2
}

# ---------- 找保存后的正式区文件 ----------
$libraryDir = Join-Path $DataDir 'Library'
$saved = Get-ChildItem -LiteralPath $libraryDir -Recurse -Filter '*.epub' -File | Select-Object -First 1

if ($null -eq $saved) {
    Write-Host "在 $libraryDir 下找不到保存后的 EPUB" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "保存后的文件：$($saved.FullName)（$([math]::Round($saved.Length / 1KB, 1)) KB）"
Write-Host ""

# ---------- 第 4 步：独立跑 EpubCheck ----------
$java = Join-Path $repoRoot 'Tools\JRE\bin\java.exe'
$jar = Join-Path $repoRoot 'Tools\EpubCheck\epubcheck.jar'
$jsonOut = Join-Path $DataDir 'Temp\accept-epubcheck.json'
New-Item -ItemType Directory -Force -Path (Split-Path $jsonOut) | Out-Null
Remove-Item -LiteralPath $jsonOut -ErrorAction SilentlyContinue

& $java -jar $jar $saved.FullName --json $jsonOut --quiet | Out-Null

$fatal = -1
$errorCount = -1
$warning = -1

if (Test-Path -LiteralPath $jsonOut) {
    $report = [System.IO.File]::ReadAllText($jsonOut, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $fatal = [int]$report.checker.nFatal
    $errorCount = [int]$report.checker.nError
    $warning = [int]$report.checker.nWarning
}

Add-Check 'epubcheck-独立跑-fatal-0' ($fatal -eq 0) "nFatal=$fatal"
Add-Check 'epubcheck-独立跑-error-0' ($errorCount -eq 0) "nError=$errorCount（warning=$warning）"

# ---------- 第 5 步：独立读元数据 + 导封面（真 Calibre） ----------
$env:CALIBRE_OVERRIDE_LANG = 'en'
$calibre = Join-Path $repoRoot 'Tools\Calibre Portable\Calibre\ebook-meta.exe'
$metaText = ''
$coverOut = Join-Path $DataDir 'Temp\accept-cover.jpg'
Remove-Item -LiteralPath $coverOut -ErrorAction SilentlyContinue

try {
    $metaText = (& $calibre $saved.FullName 2>&1 | Out-String)
    & $calibre $saved.FullName --get-cover $coverOut 2>&1 | Out-Null
}
catch {
    $metaText = "ebook-meta 调用失败：$_"
}

Add-Check 'calibre-读到改后的书名' ($metaText.Contains($expectedTitle)) `
    ("期望书名：$expectedTitle；实际 Title 行：" + (($metaText -split "`n" | Where-Object { $_ -match '^\s*Title\s*:' } | Select-Object -First 1) -replace '\s+', ' '))

$coverBytes = if (Test-Path -LiteralPath $coverOut) { (Get-Item -LiteralPath $coverOut).Length } else { 0 }
Add-Check 'calibre-导出封面非空' ($coverBytes -gt 0) "--get-cover 导出 $coverBytes 字节"

$authorLine = ($metaText -split "`n" | Where-Object { $_ -match '^\s*Author' } | Select-Object -First 1)
Add-Check 'calibre-读到新增的作者' ($metaText.Contains($expectedAuthor)) `
    ("期望作者：$expectedAuthor；实际 " + ($authorLine -replace '\s+', ' '))

$opfText = Get-OpfText $saved.FullName
Add-Check 'opf-写了新增的作者' ($opfText.Contains($expectedAuthor)) "OPF 里能找到「$expectedAuthor」=$($opfText.Contains($expectedAuthor))"
Add-Check 'opf-写了新的系列' ($opfText.Contains($expectedSeries)) "OPF 里能找到「$expectedSeries」=$($opfText.Contains($expectedSeries))"

# ---------- 第 6 步：逐条目 diff ----------
$before = Get-EntryHashes $source
$after = Get-EntryHashes $saved.FullName

$changed = New-Object System.Collections.ArrayList
foreach ($key in $before.Keys) {
    if (-not $after.ContainsKey($key)) { [void]$changed.Add("删除 $key") }
    elseif ($before[$key] -ne $after[$key]) { [void]$changed.Add("修改 $key") }
}

foreach ($key in $after.Keys) {
    if (-not $before.ContainsKey($key)) { [void]$changed.Add("新增 $key") }
}

$unexpected = @($changed | Where-Object { $_ -notmatch '\.opf$' -and $_ -notmatch '\.(jpg|jpeg|png|gif|webp)$' })
$detail = if ($changed.Count -eq 0) { "没有任何条目变化" } else { ($changed -join '；') }

Add-Check 'diff-只有预期条目变化' ($unexpected.Count -eq 0) ("变化 " + $changed.Count + " 个：" + $detail)

# ---------- 汇总 ----------
$passed = @($checks | Where-Object { $_.Ok }).Count
$total = $checks.Count
$result = [pscustomobject]@{
    File      = $source
    Saved     = $saved.FullName
    Passed    = $passed
    Total     = $total
    Fatal     = $fatal
    Errors    = $errorCount
    Warnings  = $warning
    Changed   = @($changed)
    Checks    = $checks
}

$reportPath = Join-Path $DataDir 'Temp\accept-report.json'
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ""
Write-Host ("验收结果：$passed/$total 通过" + $(if ($passed -eq $total) { ' ' } else { ' ' })) -ForegroundColor $(if ($passed -eq $total) { 'Green' } else { 'Red' })
Write-Host "报告：$reportPath"

if ($temporary -and -not $KeepDataDir) {
    try { Remove-Item -LiteralPath $DataDir -Recurse -Force } catch { }
}

if ($passed -eq $total) { exit 0 } else { exit 2 }
