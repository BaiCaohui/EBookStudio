# T7-5 全量回归 / 快速回归脚本
#
# quick（默认由 build\quick-regression.ps1 调用）：
#   造 A01 一个样本 -> 干净数据目录 -> 跑 --smoke-t75（导入 -> 阅读 -> 转 MOBI -> 改元数据 -> 保存 -> EpubCheck）
#   -> 再用**独立于被测程序**的手段复核（真 EpubCheck / 真 Calibre ebook-meta / ZIP 布局 / 源样本未被改动）
#
# full：
#   1) dotnet build（0 错误才继续）
#   2) dotnet test（902 条单元/集成测试）
#   3) make-test-samples.ps1 造 40 个样本
#   4) --smoke-t75 跑 40 样本闭环
#   5) ui-token-audit.ps1 / atomic-write-audit.ps1（静态审计）
#   6) 依次复跑既有验收套件（t61/t62/t63/t64/t65/t66/t71/t72/t73/t73b/t74 + vs1/vs2/t57/reader/txt/templates）
#   7) 合成 build\t75-regression-report.json 与 .md
#
# 目录约定：
#   -CorpusDir  样本目录（含 manifest.json），默认 Data-Test\t75-corpus
#   -DataDir    被测进程的数据目录（App 用 --datadir 指过去），默认 Data-Test\t75-<Scope>
#
# 退出码：0 = 全过；2 = 有检查失败；1 = 跑不起来。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\regression-t75.ps1 -Scope quick
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\regression-t75.ps1 -Scope full

[CmdletBinding()]
param(
    [ValidateSet('quick', 'full')]
    [string]$Scope = 'full',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$ExePath,
    [string]$CorpusDir,
    [string]$DataDir,

    # 跳过构建（刚构建过 / 指定了发布件时用）
    [switch]$SkipBuild,

    # full 专用：跳过 dotnet test
    [switch]$SkipTests,

    # full 专用：只跑这几组（逗号分隔的套件名，见 $suiteRuns）；空 = 全部
    [string]$Suites,

    [switch]$KeepDataDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$startedAt = Get-Date

$checks = New-Object System.Collections.Generic.List[object]
$suiteRuns = New-Object System.Collections.Generic.List[object]
$sampleRows = $null
$smokeDetail = $null

function Add-Check([string]$group, [string]$name, [bool]$ok, [string]$detail) {
    $script:checks.Add([pscustomobject]@{ Group = $group; Name = $name; Ok = $ok; Detail = $detail })
    $mark = if ($ok) { 'OK  ' } else { 'FAIL' }
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1,-30} {2}" -f $mark, $name, $detail) -ForegroundColor $color
}

function Resolve-Exe([string]$explicitPath, [string]$configuration) {
    if (-not [string]::IsNullOrWhiteSpace($explicitPath)) {
        return (Resolve-Path -LiteralPath $explicitPath).ProviderPath
    }
    return (Join-Path $repoRoot "src\EbookToolSuite.App\bin\$configuration\net10.0-windows\EBookStudio.exe")
}

# 按 t74 的经验：不用重定向（PS 5.1 下 Start-Process 重定向会让 ExitCode 变 $null）
function Invoke-StudioProcess([string]$exePath, [string]$arguments, [int]$timeoutMs) {
    $process = Start-Process -FilePath $exePath -ArgumentList $arguments -PassThru
    $finished = $process.WaitForExit($timeoutMs)
    if (-not $finished) {
        try { $process.Kill() } catch { }
        return -1
    }
    $process.WaitForExit()
    return [int]$process.ExitCode
}

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-EpubCheckCounts([string]$epubPath, [string]$jsonOut) {
    $java = Join-Path $repoRoot 'Tools\JRE\bin\java.exe'
    $jar = Join-Path $repoRoot 'Tools\EpubCheck\epubcheck.jar'
    if (-not (Test-Path -LiteralPath $java) -or -not (Test-Path -LiteralPath $jar)) {
        return [pscustomobject]@{ Ok = $false; Fatal = -1; Errors = -1; Warnings = -1; Reason = '工具缺失（Tools\JRE 或 Tools\EpubCheck）' }
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $jsonOut) | Out-Null
    Remove-Item -LiteralPath $jsonOut -ErrorAction SilentlyContinue
    & $java -jar $jar $epubPath --json $jsonOut --quiet | Out-Null

    if (-not (Test-Path -LiteralPath $jsonOut)) {
        return [pscustomobject]@{ Ok = $false; Fatal = -1; Errors = -1; Warnings = -1; Reason = '没生成 JSON 报告' }
    }

    $report = [System.IO.File]::ReadAllText($jsonOut, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $fatal = [int]$report.checker.nFatal
    $errors = [int]$report.checker.nError
    $warnings = [int]$report.checker.nWarning
    return [pscustomobject]@{
        Ok       = ($fatal -eq 0 -and $errors -eq 0)
        Fatal    = $fatal
        Errors   = $errors
        Warnings = $warnings
        Reason   = ''
    }
}

function Test-MimetypeLayout([string]$epubPath) {
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
        $zip = [System.IO.Compression.ZipFile]::OpenRead($epubPath)
        try {
            $first = $zip.Entries | Select-Object -First 1
            if ($null -eq $first -or $first.FullName -ne 'mimetype') { return $false }
            if ($first.CompressedLength -ne $first.Length) { return $false }
            $reader = New-Object System.IO.StreamReader($first.Open())
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            return ($text -eq 'application/epub+zip')
        }
        finally { $zip.Dispose() }
    }
    catch {
        return $false
    }
}

# =====================================================================
# 0) 准备
# =====================================================================
$exe = Resolve-Exe $ExePath $Configuration

if ([string]::IsNullOrWhiteSpace($CorpusDir)) {
    if ($Scope -eq 'quick') { $CorpusDir = Join-Path $repoRoot 'Data-Test\t75-quick-corpus' }
    else { $CorpusDir = Join-Path $repoRoot 'Data-Test\t75-corpus' }
}
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $repoRoot ("Data-Test\t75-" + $Scope)
}

$CorpusDir = [IO.Path]::GetFullPath($CorpusDir)
$DataDir = [IO.Path]::GetFullPath($DataDir)
$makeSamples = Join-Path $PSScriptRoot 'make-test-samples.ps1'

Write-Host ''
Write-Host ('===== T7-5 {0}回归 =====' -f $(if ($Scope -eq 'quick') { '快速' } else { '全量' })) -ForegroundColor Cyan
Write-Host ("可执行文件：$exe")
Write-Host ("样本目录  ：$CorpusDir")
Write-Host ("数据目录  ：$DataDir")
Write-Host ''

# =====================================================================
# 1) 构建
# =====================================================================
if (-not $SkipBuild -and [string]::IsNullOrWhiteSpace($ExePath)) {
    Write-Host '== 构建（dotnet build） ==' -ForegroundColor Cyan
    $buildOut = & dotnet build (Join-Path $repoRoot 'EbookToolSuite.sln') -c $Configuration --nologo 2>&1
    $buildExit = $LASTEXITCODE
    $buildText = ($buildOut | Out-String)
    if ($buildExit -ne 0) {
        Write-Host $buildText
        Add-Check '构建' 'dotnet-build' $false "exit=$buildExit"
        Write-Host '构建失败，后续步骤没有意义。' -ForegroundColor Red
        exit 1
    }

    $warningsZero = $buildText -match '0\s*个警告|0\s*Warning'
    Add-Check '构建' 'dotnet-build-0-warning' $warningsZero ("exit=0；" + $(if ($warningsZero) { '0 警告' } else { '警告数不是 0（见下方构建输出）' }))
    if (-not $warningsZero) { Write-Host $buildText }
}
else {
    Write-Host '== 构建（跳过） ==' -ForegroundColor DarkGray
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "找不到主程序：$exe" -ForegroundColor Red
    Add-Check '构建' 'exe-exists' $false $exe
    exit 1
}

# =====================================================================
# 2) full：单元/集成测试
# =====================================================================
if ($Scope -eq 'full' -and -not $SkipTests) {
    Write-Host ''
    Write-Host '== 单元/集成测试（dotnet test） ==' -ForegroundColor Cyan
    $testOut = & dotnet test (Join-Path $repoRoot 'EbookToolSuite.sln') -c $Configuration --no-build --nologo 2>&1
    $testExit = $LASTEXITCODE
    $testText = ($testOut | Out-String)
    if ($testExit -ne 0) { Write-Host $testText }

    # 汇总行形如「已通过! - 失败: 0，通过: 902，已跳过: 0，总计: 902」
    $summary = ($testText -split "`n" | Where-Object { $_ -match '已通过|Passed!|失败:|Failed:' } | Select-Object -Last 1)
    Add-Check '测试' 'dotnet-test' ($testExit -eq 0) ("exit=$testExit；" + ($summary -replace '\s+', ' ').Trim())
}

# =====================================================================
# 3) 造样本
# =====================================================================
Write-Host ''
Write-Host '== 造样本（make-test-samples.ps1） ==' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $makeSamples)) {
    Write-Host "缺少样本生成脚本：$makeSamples" -ForegroundColor Red
    Add-Check '样本' 'make-test-samples' $false '脚本不存在'
    exit 1
}

$onlyIds = ''
if ($Scope -eq 'quick') { $onlyIds = 'A01' }
$genArgs = @{ OutputDir = $CorpusDir }
if ($onlyIds -ne '') { $genArgs['Only'] = $onlyIds }
if ($Scope -eq 'full') { $genArgs['Force'] = $true }

& $makeSamples @genArgs
$genExit = $LASTEXITCODE
Add-Check '样本' 'make-test-samples' ($genExit -eq 0) "exit=$genExit；目录 $CorpusDir"

if ($genExit -ne 0) {
    Write-Host '样本生成失败，回归无法继续。' -ForegroundColor Red
    exit 1
}

$manifestPath = Join-Path $CorpusDir 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    Add-Check '样本' 'manifest-exists' $false $manifestPath
    exit 1
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$manifestCount = @($manifest.samples).Count
$expectedCount = if ($Scope -eq 'quick') { 1 } else { 40 }
Add-Check '样本' 'manifest-count' ($manifestCount -eq $expectedCount) ("$manifestCount 个样本（期望 $expectedCount）")

# =====================================================================
# 4) 进程内 40 样本闭环
# =====================================================================
Write-Host ''
Write-Host ('== 样本闭环（--smoke-t75，{0} 个样本） ==' -f $manifestCount) -ForegroundColor Cyan

if (-not $KeepDataDir -and (Test-Path -LiteralPath $DataDir)) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

$arguments = '--smoke-t75="' + $CorpusDir + '" --datadir="' + $DataDir + '"'
if ($Scope -eq 'quick') { $arguments = $arguments + ' --smoke-t75-filter=A01' }

$timeoutMs = if ($Scope -eq 'quick') { 600000 } else { 3600000 }
$smokeExit = Invoke-StudioProcess $exe $arguments $timeoutMs

$detailPath = Join-Path $DataDir 'Temp\t75-regression.json'
$smokePath = Join-Path $DataDir 'Temp\t75-smoke.json'

if ($smokeExit -eq -1) {
    Add-Check '样本闭环' 'smoke-t75-timeout' $false ("超时（{0} s）" -f [int]($timeoutMs / 1000))
}
elseif (-not (Test-Path -LiteralPath $detailPath)) {
    Add-Check '样本闭环' 'smoke-t75-report' $false ("没有生成报告：$detailPath（退出码 $smokeExit）")

    $logDir = Join-Path $DataDir 'Logs'
    $latestLog = Get-ChildItem -Path $logDir -Filter 'app-*.log' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latestLog) {
        Write-Host ("最近日志：" + $latestLog.FullName) -ForegroundColor Yellow
        Get-Content -LiteralPath $latestLog.FullName -Tail 40 | ForEach-Object { Write-Host $_ }
    }
    exit 1
}
else {
    $smokeDetail = Get-Content -LiteralPath $detailPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $sampleRows = @($smokeDetail.samples)

    $failed = @($sampleRows | Where-Object { @($_.failures).Count -gt 0 })
    $firstFailure = if ($failed.Count -gt 0) {
        ($failed[0].id + '：' + (@($failed[0].failures) -join ' | '))
    }
    else { '全部跑通' }

    Add-Check '样本闭环' 'smoke-t75-exit-code' ($smokeExit -eq 0) ("exit=$smokeExit；" + $firstFailure)
    Add-Check '样本闭环' 'smoke-t75-all-samples' ($failed.Count -eq 0) ("$($sampleRows.Count - $failed.Count)/$($sampleRows.Count) 个样本跑通")

    if ($Scope -eq 'full' -and $sampleRows.Count -ne 40) {
        Add-Check '样本闭环' 'smoke-t75-sample-count' $false "$($sampleRows.Count) 个样本（期望 40）"
    }
}

# =====================================================================
# 5) 独立复核（quick 的 6 步判据 + full 也做一次）
# =====================================================================
Write-Host ''
Write-Host '== 独立复核（真 EpubCheck / 真 Calibre / ZIP 布局 / 源样本） ==' -ForegroundColor Cyan

$corpusBeforeHash = Get-Sha256 (Join-Path $CorpusDir 'A\epub3-min.epub')
$libraryDir = Join-Path $DataDir 'Library'
$a01Row = @($sampleRows | Where-Object { $_.id -eq 'A01' }) | Select-Object -First 1

# 优先用报告里 A01 编辑保存那一步记下的产物路径（full 跑 40 本时"最新写的 EPUB"不是 A01）
$savedEpub = $null
if ($null -ne $a01Row -and $null -ne $a01Row.editSave -and -not [string]::IsNullOrWhiteSpace($a01Row.editSave.outputPath)) {
    $candidate = [string]$a01Row.editSave.outputPath
    if (Test-Path -LiteralPath $candidate) {
        $savedEpub = Get-Item -LiteralPath $candidate
    }
}
if ($null -eq $savedEpub) {
    $savedEpub = Get-ChildItem -LiteralPath $libraryDir -Recurse -Filter '*.epub' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

if ($null -eq $savedEpub) {
    Add-Check '独立复核' 'library-epub-exists' $false "在 $libraryDir 下找不到 EPUB"
}
else {
    Add-Check '独立复核' 'library-epub-exists' $true ("$($savedEpub.FullName)（$([math]::Round($savedEpub.Length / 1KB, 1)) KB）")

    # ① 真 EpubCheck 独立跑
    $epubCheckJson = Join-Path $DataDir 'Temp\t75-independent-epubcheck.json'
    $check = Get-EpubCheckCounts $savedEpub.FullName $epubCheckJson
    Add-Check '独立复核' 'epubcheck-fatal-0-error-0' $check.Ok ("nFatal=$($check.Fatal) nError=$($check.Errors) nWarning=$($check.Warnings) $($check.Reason)")

    # ② 书名读回是「回归改名-<样本 id>」
    $calibre = Join-Path $repoRoot 'Tools\Calibre Portable\Calibre\ebook-meta.exe'
    if (Test-Path -LiteralPath $calibre) {
        $env:CALIBRE_OVERRIDE_LANG = 'en'
        $metaText = (& $calibre $savedEpub.FullName 2>&1 | Out-String)
        $titleLine = ($metaText -split "`n" | Where-Object { $_ -match '^\s*Title\s*:' } | Select-Object -First 1)
        $titleOk = $metaText -match '回归改名-'
        Add-Check '独立复核' 'calibre-title-written-back' $titleOk (($titleLine -replace '\s+', ' ').Trim())
    }
    else {
        Add-Check '独立复核' 'calibre-title-written-back' $false '找不到 Tools\Calibre Portable\Calibre\ebook-meta.exe'
    }

    # ③ mimetype 首条 + STORED + 内容精确（A12/A13 的规范化的通用口径）
    Add-Check '独立复核' 'epub-mimetype-layout' (Test-MimetypeLayout $savedEpub.FullName) '首条 mimetype / 未压缩 / application/epub+zip'

    # ④ 转换产物（quick 判据里的「转 MOBI」）
    $mobi = Get-ChildItem -LiteralPath $libraryDir -Recurse -Filter '*.mobi' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $mobi) {
        Add-Check '独立复核' 'convert-mobi-output' $false '找不到 .mobi 产物'
    }
    else {
        Add-Check '独立复核' 'convert-mobi-output' ($mobi.Length -gt 0) ("$($mobi.Name)（$([math]::Round($mobi.Length / 1KB, 1)) KB）")
    }
}

# ⑤ 源样本没有被原地改动（导入/编辑都必须只碰库里副本）
$corpusAfterHash = Get-Sha256 (Join-Path $CorpusDir 'A\epub3-min.epub')
Add-Check '独立复核' 'corpus-source-untouched' ($corpusBeforeHash -eq $corpusAfterHash) ("A01 SHA256 前 8 位 $($corpusBeforeHash.Substring(0,8)) -> $($corpusAfterHash.Substring(0,8))")

# ⑥ 阅读步骤确实发生过（导入后的书打开了、首章物化出正文）
if ($null -eq $a01Row) {
    Add-Check '独立复核' 'read-step-ran' $false '报告里没有 A01 行'
}
else {
    Add-Check '独立复核' 'read-step-ran' ([bool]$a01Row.readOk) ("阅读=" + $a01Row.readOk + "（" + $a01Row.readDetail + "）")
}

# =====================================================================
# 6) full：静态审计 + 既有验收套件
# =====================================================================
if ($Scope -eq 'full') {

    $auditRuns = @(
        @{ Name = 'ui-token-audit';    Script = 'ui-token-audit.ps1' },
        @{ Name = 'atomic-write-audit'; Script = 'atomic-write-audit.ps1' }
    )

    Write-Host ''
    Write-Host '== 静态审计 ==' -ForegroundColor Cyan
    foreach ($audit in $auditRuns) {
        $scriptPath = Join-Path $PSScriptRoot $audit.Script
        if (-not (Test-Path -LiteralPath $scriptPath)) {
            Add-Check '静态审计' $audit.Name $false '脚本不存在'
            continue
        }
        $auditOut = & powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath 2>&1
        $auditExit = $LASTEXITCODE
        $tail = (($auditOut | Select-Object -Last 2) -join ' / ') -replace '[\r\n]+', ' '
        Add-Check '静态审计' $audit.Name ($auditExit -eq 0) ("exit=$auditExit；$tail")
    }

    # 既有验收套件：每个都是一次独立进程 + 自己的数据目录。
    # 顺序按「便宜的先跑，贵的在后」，一个套件失败不阻断后面的（失败会记录在案）。
    $allSuites = @(
        @{ Name = 't73';   Script = 'smoke-t73.ps1';   Args = @{ Configuration = $Configuration; WorkDir = (Join-Path $repoRoot 'Data-Test\t73-work') } },
        @{ Name = 't74';   Script = 'smoke-t74.ps1';   Args = @{ Configuration = $Configuration; SkipBuild = $true } },
        @{ Name = 't73b';  Script = 'smoke-t73b.ps1';  Args = @{ Configuration = $Configuration; SkipTests = $true } },
        @{ Name = 't61';   Script = 'smoke-t61.ps1';   Args = @{ Configuration = $Configuration; FixtureDir = (Join-Path $repoRoot 'Data-Test\t61-fixtures'); RebuildFixtures = $true } },
        @{ Name = 't63';   Script = 'smoke-t63.ps1';   Args = @{ Configuration = $Configuration } },
        @{ Name = 't64';   Script = 'smoke-t64.ps1';   Args = @{ Configuration = $Configuration } },
        @{ Name = 't65';   Script = 'smoke-t65.ps1';   Args = @{ Configuration = $Configuration } },
        @{ Name = 't62';   Script = 'smoke-t62.ps1';   Args = @{ Configuration = $Configuration; PdfDir = (Join-Path $repoRoot 'TestCorpus') } },
        @{ Name = 't62b';  Script = 'smoke-t62b.ps1';  Args = @{ Configuration = $Configuration; PdfDir = (Join-Path $repoRoot 'TestCorpus') } },
        @{ Name = 't71';   Script = 'smoke-t71.ps1';   Args = @{ Configuration = $Configuration } },
        @{ Name = 't72';   Script = 'smoke-t72.ps1';   Args = @{ Configuration = $Configuration } },
        @{ Name = 't66';   Script = 'smoke-t66.ps1';   Args = @{ Configuration = $Configuration; PdfDir = (Join-Path $repoRoot 'TestCorpus') } },

        # 早期阶段的验收套件：附录 A 的阶段 1/2/4/5 条目要的是"本次复跑"的证据，
        # 样本一律用 T7-5 自己的 40 样本，不再依赖用户手上的真实书籍。
        @{ Name = 'vs1';       Script = 'smoke-vs1.ps1';       Args = @{ Configuration = $Configuration; File = (Join-Path $CorpusDir 'A\epub3-min.epub') } },
        @{ Name = 'vs2';       Script = 'smoke-vs2.ps1';       Args = @{ Configuration = $Configuration; File = (Join-Path $CorpusDir 'A\epub3-min.epub'); File2 = (Join-Path $CorpusDir 'B\txt-utf8.txt'); ScrollSeconds = 30 } },
        @{ Name = 't57';       Script = 'smoke-t57.ps1';       Args = @{ Configuration = $Configuration; EditFile = (Join-Path $CorpusDir 'A\epub2-min.epub'); ReaderFile = (Join-Path $CorpusDir 'A\epub3-min.epub'); SkipTests = $true } },
        @{ Name = 'reader';    Script = 'smoke-reader.ps1';    Args = @{ Configuration = $Configuration; File = (Join-Path $CorpusDir 'A\epub3-multi-chapter.epub') } },
        @{ Name = 'txt';       Script = 'smoke-txt.ps1';       Args = @{ Configuration = $Configuration; File = (Join-Path $CorpusDir 'B\txt-gbk.txt') } },
        @{ Name = 'templates'; Script = 'smoke-templates.ps1'; Args = @{ Configuration = $Configuration; Epub = (Join-Path $CorpusDir 'A\epub2-min.epub'); Epub2 = (Join-Path $CorpusDir 'A\epub3-min.epub') } }
    )

    $selected = $allSuites
    if (-not [string]::IsNullOrWhiteSpace($Suites)) {
        $wanted = @($Suites.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
        $selected = @($allSuites | Where-Object { $wanted -contains $_.Name })
    }

    Write-Host ''
    Write-Host ('== 既有验收套件（{0} 个） ==' -f $selected.Count) -ForegroundColor Cyan

    foreach ($suite in $selected) {
        $scriptPath = Join-Path $PSScriptRoot $suite.Script
        if (-not (Test-Path -LiteralPath $scriptPath)) {
            Add-Check '既有套件' $suite.Name $false '脚本不存在'
            $suiteRuns.Add([pscustomobject]@{ Name = $suite.Name; ExitCode = -2; Ok = $false; Seconds = 0 })
            continue
        }

        $suiteWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $suiteArgs = @{}
        foreach ($key in $suite.Args.Keys) { $suiteArgs[$key] = $suite.Args[$key] }
        if ($suiteArgs.ContainsKey('SkipBuild')) { $suiteArgs.Remove('SkipBuild') }

        # 套件都是自己解析 -ExePath / -Configuration 的脚本，统一用 powershell 起子进程，
        # 这样脚本内部的 exit code 能原样回来。
        $argLine = ''
        foreach ($key in $suiteArgs.Keys) {
            $value = $suiteArgs[$key]
            if ($value -is [bool]) {
                if ($value) { $argLine = $argLine + ' -' + $key }
            }
            else {
                $argLine = $argLine + ' -' + $key + ' "' + [string]$value + '"'
            }
        }

        Write-Host ("-- 套件 {0} ..." -f $suite.Name) -ForegroundColor DarkCyan
        $suiteProc = Start-Process -FilePath 'powershell' `
            -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $scriptPath + '"' + $argLine) `
            -PassThru -NoNewWindow
        $suiteDone = $suiteProc.WaitForExit(3600000)
        if (-not $suiteDone) {
            try { $suiteProc.Kill() } catch { }
            $suiteExit = -1
        }
        else {
            $suiteProc.WaitForExit()
            $suiteExit = [int]$suiteProc.ExitCode
        }

        $suiteWatch.Stop()
        $suiteOk = ($suiteExit -eq 0)
        $suiteRuns.Add([pscustomobject]@{
            Name     = $suite.Name
            ExitCode = $suiteExit
            Ok       = $suiteOk
            Seconds  = [int]$suiteWatch.Elapsed.TotalSeconds
        })
        Add-Check '既有套件' $suite.Name $suiteOk ("exit=$suiteExit；{0} s" -f [int]$suiteWatch.Elapsed.TotalSeconds)
    }
}

# =====================================================================
# 7) 汇总报告
# =====================================================================
$finishedAt = Get-Date
$passed = @($checks | Where-Object { $_.Ok }).Count
$total = $checks.Count
$failedChecks = @($checks | Where-Object { -not $_.Ok })
$allOk = ($total -gt 0) -and ($failedChecks.Count -eq 0)

$reportBase = if ($Scope -eq 'quick') { 't75-quick-report' } else { 't75-regression-report' }
$jsonPath = Join-Path $PSScriptRoot ($reportBase + '.json')
$mdPath = Join-Path $PSScriptRoot ($reportBase + '.md')

$json = [pscustomobject]@{
    Task        = ('T7-5 {0}回归' -f $(if ($Scope -eq 'quick') { '快速' } else { '全量' }))
    Scope       = $Scope
    StartedAt   = $startedAt.ToString('o')
    FinishedAt  = $finishedAt.ToString('o')
    DurationMs  = [int](($finishedAt - $startedAt).TotalMilliseconds)
    Configuration = $Configuration
    ExePath     = $exe
    CorpusDir   = $CorpusDir
    DataDir     = $DataDir
    Passed      = $allOk
    PassedCount = $passed
    Total       = $total
    Checks      = $checks
    Suites      = $suiteRuns
    Samples     = $sampleRows
    SampleSummary = if ($null -ne $smokeDetail) { $smokeDetail.summary } else { $null }
}
$json | ConvertTo-Json -Depth 8 | Out-File -LiteralPath $jsonPath -Encoding utf8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine(('# T7-5 {0}回归 · 报告' -f $(if ($Scope -eq 'quick') { '快速' } else { '全量' })))
[void]$md.AppendLine('')
[void]$md.AppendLine('- 时间：' + $startedAt.ToString('yyyy-MM-dd HH:mm:ss') + ' → ' + $finishedAt.ToString('HH:mm:ss') + '（' + $json.DurationMs + ' ms）')
[void]$md.AppendLine('- 配置：' + $Configuration + '；可执行文件：`' + $exe + '`')
[void]$md.AppendLine('- 样本目录：`' + $CorpusDir + '`；数据目录：`' + $DataDir + '`')
[void]$md.AppendLine('- 结果：**' + $passed + '/' + $total + ' 通过**')
[void]$md.AppendLine('- 报告：本文件 + `' + (Split-Path -Leaf $jsonPath) + '`；冒烟原始明细 `' + $detailPath + '`')
[void]$md.AppendLine('')
[void]$md.AppendLine('## 检查项')
[void]$md.AppendLine('')
[void]$md.AppendLine('| 分组 | 检查项 | 结果 | 说明 |')
[void]$md.AppendLine('|------|--------|------|------|')
foreach ($check in $checks) {
    $state = if ($check.Ok) { 'PASS' } else { 'FAIL' }
    $detail = ($check.Detail -replace '\|', '\|') -replace '[\r\n]+', ' '
    [void]$md.AppendLine('| ' + $check.Group + ' | ' + $check.Name + ' | ' + $state + ' | ' + $detail + ' |')
}
[void]$md.AppendLine('')

if ($null -ne $sampleRows -and $sampleRows.Count -gt 0) {
    [void]$md.AppendLine('## 40 样本矩阵')
    [void]$md.AppendLine('')
    [void]$md.AppendLine('| 样本 | 组 | 格式 | 导入 | 阅读 | 转换 | 编辑保存 | 校验 | 耗时(ms) | 失败项 |')
    [void]$md.AppendLine('|------|----|------|------|------|------|----------|------|----------|--------|')
    foreach ($row in $sampleRows) {
        $importCell = [string]$row.importOutcome
        if ($null -ne $row.importOk) { $importCell = $importCell + $(if ($row.importOk) { ' ✔' } else { ' ✘' }) }
        $readCell = if ($null -ne $row.readOk) { if ($row.readOk) { 'ok' } else { 'fail' } } else { '-' }
        $convertCell = if (@($row.convert).Count -gt 0) {
            (@($row.convert) | ForEach-Object { $_.step + ':' + $(if ($_.ok) { 'ok' } else { 'fail' }) }) -join ' '
        } else { '-' }
        $editCell = if ($null -ne $row.editSave) { if ($row.editSave.ok) { 'ok' } else { 'fail' } } else { '-' }
        $valCell = if ($null -ne $row.validate) { if ($row.validate.ok) { 'ok' } else { 'fail' } } else { '-' }
        $failCell = (@($row.failures) -join '；') -replace '\|', '\|'
        $failCell = $failCell -replace '[\r\n]+', ' '
        [void]$md.AppendLine('| ' + $row.id + ' | ' + $row.group + ' | ' + $row.format + ' | ' + $importCell + ' | ' + $readCell + ' | ' + $convertCell + ' | ' + $editCell + ' | ' + $valCell + ' | ' + $row.elapsedMs + ' | ' + $failCell + ' |')
    }
    [void]$md.AppendLine('')

    $summary = $smokeDetail.summary
    if ($null -ne $summary) {
        [void]$md.AppendLine('## 样本汇总')
        [void]$md.AppendLine('')
        [void]$md.AppendLine('- 样本：' + $summary.passedSamples + '/' + $summary.sampleCount + ' 跑通（失败 ' + $summary.failedSamples + '）')
        [void]$md.AppendLine('- 转换：' + $summary.convertPassed + '/' + $summary.convertCount + ' 成功')
        [void]$md.AppendLine('- 编辑保存：' + $summary.editSavePassed + '/' + $summary.editSaveCount + ' 通过')
        [void]$md.AppendLine('- EpubCheck：' + $summary.validatePassed + '/' + $summary.validateCount + ' 零 error')
        [void]$md.AppendLine('- 样本总字节：' + ('{0:N0}' -f $summary.corpusBytes) + ' B；样本闭环总耗时 ' + $summary.elapsedMs + ' ms')
        [void]$md.AppendLine('')
    }
}

if ($suiteRuns.Count -gt 0) {
    [void]$md.AppendLine('## 既有验收套件复跑')
    [void]$md.AppendLine('')
    [void]$md.AppendLine('| 套件 | 退出码 | 结果 | 耗时(s) |')
    [void]$md.AppendLine('|------|--------|------|---------|')
    foreach ($suite in $suiteRuns) {
        [void]$md.AppendLine('| ' + $suite.Name + ' | ' + $suite.ExitCode + ' | ' + $(if ($suite.Ok) { 'PASS' } else { 'FAIL' }) + ' | ' + $suite.Seconds + ' |')
    }
    [void]$md.AppendLine('')
}

if ($allOk) {
    [void]$md.AppendLine('## 结论')
    [void]$md.AppendLine('')
    [void]$md.AppendLine('全部检查通过。')
}
else {
    [void]$md.AppendLine('## 未通过项')
    [void]$md.AppendLine('')
    foreach ($check in $failedChecks) {
        [void]$md.AppendLine('- **' + $check.Name + '**：' + $check.Detail)
    }
}

$md.ToString() | Out-File -LiteralPath $mdPath -Encoding utf8

Write-Host ''
if ($allOk) {
    Write-Host ('T7-5 {0}回归通过：{1}/{2}' -f $(if ($Scope -eq 'quick') { '快速' } else { '全量' }), $passed, $total) -ForegroundColor Green
    Write-Host ("报告：" + $mdPath)
    exit 0
}

Write-Host ('T7-5 {0}回归未通过：{1}/{2}' -f $(if ($Scope -eq 'quick') { '快速' } else { '全量' }), $passed, $total) -ForegroundColor Red
$failedChecks | ForEach-Object { Write-Host ('  - ' + $_.Name + '：' + $_.Detail) -ForegroundColor Red }
Write-Host ("报告：" + $mdPath)
exit 2
