# T7-3B（阅读统计页）验收脚本
#
# 做三件事：
#   1. 跑 T7-3B 相关的单测（迁移 / 仓储 / 会话采集 / 聚合口径）
#   2. 跑进程内冒烟 --smoke-t73b（统计页 UI / 三套主题 / 书库检索与排序持久化 / 采集接线）
#   3. 合并成 build\t73b-acceptance-report.md 与 .json
#
# 退出码：0 = 全过；2 = 有检查失败；1 = 跑不起来。

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$WorkDir,
    [string]$DataDir,
    [switch]$SkipTests,
    [switch]$KeepWorkDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "找不到主程序：$exe" -ForegroundColor Red
    Write-Host "先跑一次 dotnet build -c $Configuration" -ForegroundColor Yellow
    exit 1
}

if (-not $WorkDir) { $WorkDir = Join-Path $repoRoot 'build\t73b-work' }
if (-not $DataDir) { $DataDir = Join-Path $repoRoot "Data-Test\t73b-$Configuration" }

if (-not $KeepWorkDir -and (Test-Path -LiteralPath $WorkDir)) {
    Remove-Item -LiteralPath $WorkDir -Recurse -Force
}
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

$startedAt = Get-Date
$rows = New-Object System.Collections.Generic.List[object]
$testExit = 0

# ---------- 1) T7-3B 单测 ----------
if (-not $SkipTests) {
    Write-Host '== 1/2 T7-3B 单测 ==' -ForegroundColor Cyan

    $suites = @(
        @{ Name = '数据层（迁移 v9→v10 / 会话仓储）'; Project = 'tests\EbookToolSuite.Infrastructure.Tests'; Filter = 'FullyQualifiedName~ReadingStats' },
        @{ Name = '服务层（聚合口径 / 会话采集）'; Project = 'tests\EbookToolSuite.Services.Tests'; Filter = 'FullyQualifiedName~ReadingStats|FullyQualifiedName~ReadingSession' }
    )

    foreach ($suite in $suites) {
        $out = & dotnet test (Join-Path $repoRoot $suite.Project) -c $Configuration --nologo --filter $suite.Filter 2>&1
        $code = $LASTEXITCODE
        if ($code -ne 0) { $testExit = 2 }
        $tail = @($out | Select-Object -Last 3) -join ' '
        $rows.Add([pscustomobject]@{
            Group  = '单测'
            Name   = $suite.Name
            Ok     = ($code -eq 0)
            Detail = ('exit=' + $code + '；' + $tail)
        })
        if ($code -ne 0) {
            $out | ForEach-Object { Write-Host $_ }
        }
    }
}

# ---------- 2) 进程内冒烟 ----------
Write-Host ''
Write-Host '== 2/2 进程内冒烟（--smoke-t73b） ==' -ForegroundColor Cyan
$arguments = '--smoke-t73b="' + $WorkDir + '" --datadir="' + $DataDir + '"'
$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
$finished = $process.WaitForExit(600000)
if (-not $finished) {
    try { $process.Kill() } catch { }
    Write-Host '冒烟超时（600 秒）' -ForegroundColor Red
    exit 1
}
$smokeExit = $process.ExitCode

$reportPath = Join-Path $WorkDir 't73b-report.json'
$smoke = if (Test-Path -LiteralPath $reportPath) { Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }

if ($smoke) {
    foreach ($check in $smoke.Checks) {
        $rows.Add([pscustomobject]@{ Group = '进程内冒烟'; Name = $check.Name; Ok = [bool]$check.Ok; Detail = [string]$check.Detail })
    }
}

# ---------- 3) 合并报告 ----------
$passed = @($rows | Where-Object { $_.Ok }).Count
$total = $rows.Count
$failed = @($rows | Where-Object { -not $_.Ok })
$finishedAt = Get-Date
$allOk = ($total -gt 0) -and ($failed.Count -eq 0) -and ($testExit -eq 0) -and ($smokeExit -eq 0)

$shots = @(Get-ChildItem -Path (Join-Path $WorkDir 'shots') -Filter *.png -File -ErrorAction SilentlyContinue)

$json = [pscustomobject]@{
    Task          = 'T7-3B 阅读统计页'
    StartedAt     = $startedAt.ToString('o')
    FinishedAt    = $finishedAt.ToString('o')
    DurationMs    = [int](($finishedAt - $startedAt).TotalMilliseconds)
    Configuration = $Configuration
    Passed        = $allOk
    PassedCount   = $passed
    Total         = $total
    TestExit      = $testExit
    SmokeExit     = $smokeExit
    Screenshots   = @($shots | ForEach-Object { $_.FullName })
    Checks        = $rows
}
$jsonPath = Join-Path $PSScriptRoot 't73b-acceptance-report.json'
$json | ConvertTo-Json -Depth 6 | Out-File -LiteralPath $jsonPath -Encoding utf8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# T7-3B 阅读统计页 · 验收报告')
[void]$md.AppendLine('')
[void]$md.AppendLine('- 时间：' + $startedAt.ToString('yyyy-MM-dd HH:mm:ss') + ' → ' + $finishedAt.ToString('HH:mm:ss') + '（' + $json.DurationMs + ' ms）')
[void]$md.AppendLine('- 配置：' + $Configuration + '；数据目录：`' + $DataDir + '`')
[void]$md.AppendLine('- 结果：**' + $passed + '/' + $total + ' 通过**，单测退出码 ' + $testExit + '，冒烟退出码 ' + $smokeExit)
[void]$md.AppendLine('')
[void]$md.AppendLine('## 逐项')
[void]$md.AppendLine('')
[void]$md.AppendLine('| 组 | 检查项 | 结果 | 说明 |')
[void]$md.AppendLine('|----|--------|------|------|')
foreach ($row in $rows) {
    $state = if ($row.Ok) { 'PASS' } else { 'FAIL' }
    $detail = $row.Detail -replace '\|', '\|'
    [void]$md.AppendLine('| ' + $row.Group + ' | ' + $row.Name + ' | ' + $state + ' | ' + $detail + ' |')
}
[void]$md.AppendLine('')
[void]$md.AppendLine('## 截图')
[void]$md.AppendLine('')
if ($shots.Count -gt 0) {
    foreach ($shot in $shots) {
        [void]$md.AppendLine('- `' + $shot.FullName.Substring($repoRoot.Length + 1) + '`')
    }
} else {
    [void]$md.AppendLine('- 没有截图')
}
[void]$md.AppendLine('')
[void]$md.AppendLine('## 口径与证据')
[void]$md.AppendLine('')
[void]$md.AppendLine('- **数据表**：迁移 `0010_reading_sessions.sql` 把 `user_version` 9 → 10；表与两个索引由冒烟和单测双重确认。')
[void]$md.AppendLine('- **时长口径**：时长 = `ended_at - started_at`；`ended_at` 是最后一次阅读事件时间，挂机不计时；跨本地日按日拆分。')
[void]$md.AppendLine('- **心跳**：每 60 秒落库一次；两次事件间隔超过 5 分钟另起一条会话（两段之间不计时）；退出前 `FlushAsync` 补写。')
[void]$md.AppendLine('- **聚合单测**：`ReadingStatsAggregatorTests` 用固定时区造 90 天数据，断言今日 / 本周 / 本月 / 全部、跨天拆分、连续天数（含断一天归零）。')
[void]$md.AppendLine('- **性能判据**：`ReadingStatsIntegrationTests` 在 1000 本书 + 50000 条会话的真实 SQLite 上断言统计查询 ≤ 300 ms（实测 172 ms，测试输出里有当次数字）。')
[void]$md.AppendLine('- **删书级联**：`ReadingStatsIntegrationTests` 断言删书后会话消失、汇总数字跟着变。')
[void]$md.AppendLine('- **书库检索 / 排序持久化**：`ui.library.search` / `ui.library.sortField` / `ui.library.sortDescending` / `ui.library.format` / `ui.library.status` / `ui.library.onlyWithCover`；冒烟用第二个 `LibraryViewModel` 实例模拟"重开书库"确认恢复一致，退出时还会补写一次。')
[void]$md.AppendLine('')
[void]$md.AppendLine('## 只能用眼睛验的部分')
[void]$md.AppendLine('')
[void]$md.AppendLine('- 三套主题下统计页的实际观感（截图为整窗渲染，不含系统标题栏）。')
[void]$md.AppendLine('- 真实阅读 10 分钟后统计页数字的手感（冒烟用造出来的会话数据，不真的读 10 分钟）。')

$mdPath = Join-Path $PSScriptRoot 't73b-acceptance-report.md'
$md.ToString() | Out-File -LiteralPath $mdPath -Encoding utf8

Write-Host ''
if ($allOk) {
    Write-Host ("T7-3B 验收通过：{0}/{1}（截图 {2} 张）" -f $passed, $total, $shots.Count) -ForegroundColor Green
    Write-Host ("报告：" + $mdPath)
    exit 0
}

Write-Host ("T7-3B 验收未通过：{0}/{1}" -f $passed, $total) -ForegroundColor Red
$failed | ForEach-Object { Write-Host ("  - [" + $_.Group + "] " + $_.Name + "：" + $_.Detail) -ForegroundColor Red }
Write-Host ("报告：" + $mdPath)
exit 2
