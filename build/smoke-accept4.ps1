<#
.SYNOPSIS
    T4-7 阶段 4 验收（阅读：分页/滚动、主题、排版、目录、TXT、快捷键、全屏）。

.DESCRIPTION
    按执行计划 7.1 的验收脚本与 7.3 的出口条件，把"能自动验的部分"一次跑完：

      A. EPUB2（Sample Book I）阅读器冒烟   分页/滚动切换不丢位置、5 套主题（含书自带白底被覆盖）、
         排版可调且持久化、改排版位置保持、NCX 目录跳转、快捷键（含 iframe 内）、全屏、状态条
      B. EPUB3（nav 目录）阅读器冒烟  同上 + nav 解析
      C. GBK 的 TXT 冒烟             导入、编码识别、切章、手动换编码立即重排
      D. 无章节标记的 TXT 冒烟        硬切兜底（脚本会核对报告里"硬切=True"）
      E. 真样本目录集成（ETS_RUN_INTEGRATION=1） 28 本真实 EPUB 的 NCX/nav 解析与降级
      F. 100 MB TXT（ETS_RUN_BIG_TXT=1） 首屏 <= 2 秒、内存占用远小于文件

    退出码：0 = 全部通过；2 = 有失败；1 = 跑不起来。

.EXAMPLE
    powershell -File build\smoke-accept4.ps1
#>
[CmdletBinding()]
param(
    [string]$Epub2 = 'TestCorpus\sample-epub-1.epub',

    [string]$Epub3 = 'TestCorpus\sample-epub-4.epub',

    [string]$TxtGbk = 'build\samples\txt-smoke-sample-gbk.txt',

    [string]$TxtNoMarker = 'build\samples\txt-smoke-sample-nomarker.txt',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$ExePath,

    [string]$DataRoot,

    # 每个冒烟的上限（秒）
    [int]$TimeoutSeconds = 900,

    [switch]$SkipHeavy
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Resolve-RepoPath([string]$path) {
    if ([IO.Path]::IsPathRooted($path)) { return $path }
    return (Join-Path $repoRoot $path)
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $repoRoot ("build\_t47\" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}

New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null

$results = New-Object System.Collections.ArrayList

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    [void]$results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    $color = if ($ok) { 'Green' } else { 'Red' }
    $state = if ($ok) { '[OK]  ' } else { '[FAIL]' }
    Write-Host ("$state {0,-42} {1}" -f $name, $detail) -ForegroundColor $color
}

# ---------- 跑一个已有冒烟脚本，读它的报告 ----------
function Invoke-Smoke([string]$name, [string]$script, [string[]]$extraArgs, [string]$reportFile, [string[]]$detailPatterns) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repoRoot $script)) + $extraArgs

    Write-Host ''
    Write-Host ("===== {0} =====" -f $name) -ForegroundColor Cyan

    $output = & powershell @arguments 2>&1
    $code = $LASTEXITCODE
    $tail = ($output | Select-Object -Last 3) -join ' / '

    $reportPath = Join-Path $DataRoot $reportFile
    $ok = $false
    $detail = "退出码=$code；$tail"

    if (Test-Path -LiteralPath $reportPath) {
        $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $failed = @($report.Checks | Where-Object { -not $_.Ok })
        $ok = ($code -eq 0) -and ($failed.Count -eq 0)

        $patternOk = $true
        $patternNote = ''

        foreach ($want in @($detailPatterns)) {
            if ([string]::IsNullOrWhiteSpace($want)) {
                continue
            }

            # 用 -like + 强制数组，别依赖"单个对象也有 Count"这类细节
            $hits = @($report.Checks | Where-Object { $_.Detail -like ('*' + $want + '*') })

            if ($hits.Count -lt 1) {
                $patternOk = $false
                $sample = (@($report.Checks) | Select-Object -First 2 | ForEach-Object { $_.Name + '=' + $_.Detail }) -join ' ; '
                $patternNote = $patternNote + "；明细里没找到 [" + $want + "]（样本：$sample）"
            }
        }

        $ok = $ok -and $patternOk

        $detail = ("{0}/{1} 项通过；退出码={2}{3}" -f `
            (@($report.Checks | Where-Object { $_.Ok }).Count), $report.Checks.Count, $code, $patternNote)
    }

    Add-Result $name $ok $detail
    return $ok
}

# ---------- 跑一个 dotnet test 过滤集 ----------
function Invoke-TestFilter([string]$name, [string]$project, [string]$filter, [hashtable]$env) {
    Write-Host ''
    Write-Host ("===== {0} =====" -f $name) -ForegroundColor Cyan

    $saved = @{}
    foreach ($key in $env.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $env[$key])
    }

    $log = Join-Path $DataRoot (($name -replace '[^\w]', '_') + '.log')

    try {
        & dotnet test $project -c $Configuration --nologo -v q --filter $filter *>&1 |
            Tee-Object -FilePath $log | Select-String -Pattern '失败:|已通过!' | Select-Object -Last 2 | ForEach-Object { Write-Host ("    " + $_.Line.Trim()) }
    }
    finally {
        foreach ($key in $saved.Keys) {
            [Environment]::SetEnvironmentVariable($key, $saved[$key])
        }
    }

    $text = if (Test-Path $log) { Get-Content -LiteralPath $log -Raw -Encoding UTF8 } else { '' }
    $ok = ($text -match '失败:\s*0') -and ($text -match '已通过!')

    Add-Result $name $ok "日志：$log"
    return $ok
}

$exeArgs = @()
if (-not [string]::IsNullOrWhiteSpace($ExePath)) { $exeArgs = @('-ExePath', $ExePath) }

# ---------- A/B：阅读器（EPUB2 / EPUB3） ----------
Invoke-Smoke 'A-EPUB2阅读冒烟' 'build\smoke-reader.ps1' `
    (@('-File', (Resolve-RepoPath $Epub2), '-DataDir', (Join-Path $DataRoot 'reader2')) + $exeArgs) `
    'reader2\Temp\reader-smoke.json' @('状态条', '全屏=True') | Out-Null

Invoke-Smoke 'B-EPUB3阅读冒烟' 'build\smoke-reader.ps1' `
    (@('-File', (Resolve-RepoPath $Epub3), '-DataDir', (Join-Path $DataRoot 'reader3')) + $exeArgs) `
    'reader3\Temp\reader-smoke.json' @('状态条', '全屏=True') | Out-Null

# ---------- C/D：TXT（GBK / 无标记硬切） ----------
Invoke-Smoke 'C-TXT(GBK)冒烟' 'build\smoke-txt.ps1' `
    (@('-File', (Resolve-RepoPath $TxtGbk), '-DataDir', (Join-Path $DataRoot 'txt-gbk')) + $exeArgs) `
    'txt-gbk\Temp\txt-smoke.json' @('编码=GBK', '硬切=False') | Out-Null

Invoke-Smoke 'D-TXT(无标记硬切)冒烟' 'build\smoke-txt.ps1' `
    (@('-File', (Resolve-RepoPath $TxtNoMarker), '-DataDir', (Join-Path $DataRoot 'txt-raw')) + $exeArgs) `
    'txt-raw\Temp\txt-smoke.json' @('硬切=True') | Out-Null

if (-not $SkipHeavy) {
    # ---------- E：真样本目录（NCX / nav / 降级） ----------
    Invoke-TestFilter 'E-真样本目录集成(28本)' 'tests\EbookToolSuite.Engines.Tests\EbookToolSuite.Engines.Tests.csproj' `
        'FullyQualifiedName~EpubTocIntegrationTests' @{ ETS_RUN_INTEGRATION = '1' } | Out-Null

    # ---------- F：100 MB TXT ----------
    Invoke-TestFilter 'F-100MB-TXT首屏' 'tests\EbookToolSuite.Services.Tests\EbookToolSuite.Services.Tests.csproj' `
        'FullyQualifiedName~一百兆TXT' @{ ETS_RUN_BIG_TXT = '1' } | Out-Null

    # 100MB 用例会把实测数字写到 build\_t45\big-txt.txt
    $big = Join-Path $repoRoot 'build\_t45\big-txt.txt'
    if (Test-Path -LiteralPath $big) {
        $numbers = (Get-Content -LiteralPath $big -Encoding UTF8 | Where-Object { $_ -match '=' }) -join ' / '
        Add-Result 'G-100MB实测数字' $true $numbers
    }
    else {
        Add-Result 'G-100MB实测数字' $false '没找到 build\_t45\big-txt.txt'
    }
}

Write-Host ''
$failed = @($results | Where-Object { -not $_.Ok })
Write-Host ('=' * 70)
$summaryColor = 'Red'

if ($failed.Count -eq 0) {
    $summaryColor = 'Green'
}

Write-Host ("阶段 4 验收：{0}/{1} 通过" -f (@($results | Where-Object { $_.Ok }).Count), $results.Count) -ForegroundColor $summaryColor

if ($failed.Count -gt 0) {
    foreach ($item in $failed) {
        Write-Host ("  失败：{0}  {1}" -f $item.Name, $item.Detail) -ForegroundColor Red
    }

    exit 2
}

Write-Host '全部通过。' -ForegroundColor Green
exit 0
