# T7-3（UI 统一与美化）验收脚本
#
# 做三件事：
#   1. 跑静态界面审计 build\ui-token-audit.ps1（硬编码颜色 / 字形字面量 / 系统消息框 / 字号圆角越界 / 主题 key 一致 / 图标 / 控件词表 / 阅读器接线）
#   2. 跑进程内冒烟 --smoke-t73（六档主题切换 / 自定义配色 / 隐式样式 / 对话框 / 设置页 / 截图）
#   3. 合并成 build\t73-acceptance-report.md 与 .json
#
# 退出码：0 = 全过；2 = 有检查失败；1 = 跑不起来。

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$WorkDir,
    [string]$DataDir,
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

if (-not $WorkDir) { $WorkDir = Join-Path $repoRoot 'build\t73-work' }
if (-not $DataDir) { $DataDir = Join-Path $repoRoot "Data-Test\t73-$Configuration" }

if (-not $KeepWorkDir -and (Test-Path -LiteralPath $WorkDir)) {
    Remove-Item -LiteralPath $WorkDir -Recurse -Force
}
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

$startedAt = Get-Date

# ---------- 1) 静态审计 ----------
Write-Host '== 1/2 静态界面审计 ==' -ForegroundColor Cyan
$auditJson = Join-Path $WorkDir 'ui-token-audit.json'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'ui-token-audit.ps1') -RepoRoot $repoRoot -Json $auditJson
$auditExit = $LASTEXITCODE
$audit = if (Test-Path -LiteralPath $auditJson) { Get-Content -LiteralPath $auditJson -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }

# ---------- 2) 进程内冒烟 ----------
Write-Host ''
Write-Host '== 2/2 进程内冒烟（--smoke-t73） ==' -ForegroundColor Cyan
$arguments = '--smoke-t73="' + $WorkDir + '" --datadir="' + $DataDir + '"'
$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
$finished = $process.WaitForExit(600000)
if (-not $finished) {
    try { $process.Kill() } catch { }
    Write-Host '冒烟超时（600 秒）' -ForegroundColor Red
    exit 1
}
$smokeExit = $process.ExitCode

$reportPath = Join-Path $WorkDir 't73-report.json'
$smoke = if (Test-Path -LiteralPath $reportPath) { Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }

# ---------- 3) 合并报告 ----------
$rows = New-Object System.Collections.Generic.List[object]

if ($audit) {
    foreach ($check in $audit.Checks) {
        $rows.Add([pscustomobject]@{ Group = '静态审计'; Name = $check.Name; Ok = [bool]$check.Ok; Detail = [string]$check.Detail })
    }
}
if ($smoke) {
    foreach ($check in $smoke.Checks) {
        $rows.Add([pscustomobject]@{ Group = '进程内冒烟'; Name = $check.Name; Ok = [bool]$check.Ok; Detail = [string]$check.Detail })
    }
}

$passed = @($rows | Where-Object { $_.Ok }).Count
$total = $rows.Count
$failed = @($rows | Where-Object { -not $_.Ok })
$finishedAt = Get-Date
$allOk = ($total -gt 0) -and ($failed.Count -eq 0) -and ($auditExit -eq 0) -and ($smokeExit -eq 0)

$shots = @(Get-ChildItem -Path (Join-Path $WorkDir 'shots') -Filter *.png -File -ErrorAction SilentlyContinue)

$json = [pscustomobject]@{
    Task = 'T7-3 UI 统一与美化'
    StartedAt = $startedAt.ToString('o')
    FinishedAt = $finishedAt.ToString('o')
    DurationMs = [int](($finishedAt - $startedAt).TotalMilliseconds)
    Configuration = $Configuration
    Passed = $allOk
    PassedCount = $passed
    Total = $total
    StaticAuditExit = $auditExit
    SmokeExit = $smokeExit
    Screenshots = @($shots | ForEach-Object { $_.FullName })
    Checks = $rows
}
$jsonPath = Join-Path $PSScriptRoot 't73-acceptance-report.json'
$json | ConvertTo-Json -Depth 6 | Out-File -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# T7-3 UI 统一与美化 · 验收报告')
[void]$md.AppendLine('')
[void]$md.AppendLine('- 时间：' + $startedAt.ToString('yyyy-MM-dd HH:mm:ss') + ' → ' + $finishedAt.ToString('HH:mm:ss') + '（' + $json.DurationMs + ' ms）')
[void]$md.AppendLine('- 配置：' + $Configuration + '；数据目录：`' + $DataDir + '`')
[void]$md.AppendLine('- 结果：**' + $passed + '/' + $total + ' 通过**，静态审计退出码 ' + $auditExit + '，冒烟退出码 ' + $smokeExit)
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
[void]$md.AppendLine('## 只能用眼睛验的部分')
[void]$md.AppendLine('')
[void]$md.AppendLine('- 亮 / 米黄 / 暗 / 护眼四套主题与自定义配色的对比度与观感（截图为整窗渲染，不含系统标题栏）')
[void]$md.AppendLine('- 对话框打开时的初始焦点位置（冒烟里只验 IsDefault / IsCancel 与绑定）')
[void]$md.AppendLine('- 原生下拉弹层、右键菜单弹层（WPF Popup 不在窗口渲染树里，截图抓不到）')
[void]$md.AppendLine('- 100% / 150% / 200% 缩放下的实际排版')
[void]$md.AppendLine('')
[void]$md.AppendLine('## 已知未覆盖')
[void]$md.AppendLine('')
[void]$md.AppendLine('- 阅读器工具栏图标仍是文字按钮：换成 Segoe Fluent 字形需要改 reader.html 结构，本轮登记为后续项')
[void]$md.AppendLine('- 批量导入预检阶段的进度与取消按钮启用时机未改（登记在计划「登记不修」）')
[void]$md.AppendLine('- 重复扫描 / 打开书转换仍不可取消（同上）')

$mdPath = Join-Path $PSScriptRoot 't73-acceptance-report.md'
$md.ToString() | Out-File -LiteralPath $mdPath -Encoding UTF8

Write-Host ''
if ($allOk) {
    Write-Host ("T7-3 验收通过：{0}/{1}（截图 {2} 张）" -f $passed, $total, $shots.Count) -ForegroundColor Green
    Write-Host ("报告：" + $mdPath)
    exit 0
}

Write-Host ("T7-3 验收未通过：{0}/{1}" -f $passed, $total) -ForegroundColor Red
$failed | ForEach-Object { Write-Host ("  - [" + $_.Group + "] " + $_.Name + "：" + $_.Detail) -ForegroundColor Red }
Write-Host ("报告：" + $mdPath)
exit 2
