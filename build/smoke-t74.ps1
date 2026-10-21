# T7-4（首次运行向导与设置）验收脚本
#
# 做三件事：
#   1. 默认先构建 Debug（-ExePath 指发布件或 -SkipBuild 时跳过）
#   2. 跑进程内冒烟 --smoke-t74（8 组设置持久化 / 预加载 1-1000 / 向导 / 数据目录迁移 / 维护动作）
#   3. 合并成 build\t74-acceptance-report.json 与 .md
#
# 目录约定：
#   -WorkDir   冒烟工作目录（放 t74-report.json 与临时子目录），默认 Data-Test\t74-work
#   -DataDir   被测进程的数据目录（App 用 --datadir 指过去），默认 Data-Test\t74-Debug
#   两者必须分开：迁移检查会在 -WorkDir 下造"目标目录"，而目标目录不允许落在当前数据目录内。
#
# 退出码：0 = 全过；2 = 有检查失败；1 = 跑不起来。

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # 直接指定被测的可执行文件（发布产物验收用；不传就用 src 下的构建产物）
    [string]$ExePath,

    [string]$WorkDir,
    [string]$DataDir,

    # 跳过构建（CI / 刚构建过时用）
    [switch]$SkipBuild,

    [switch]$KeepWorkDir,
    [switch]$KeepDataDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $exe = Join-Path $repoRoot "src\EbookToolSuite.App\bin\$Configuration\net10.0-windows\EBookStudio.exe"
}
else {
    $exe = (Resolve-Path -LiteralPath $ExePath).ProviderPath
}

if (-not $WorkDir) { $WorkDir = Join-Path $repoRoot 'Data-Test\t74-work' }
if (-not $DataDir) { $DataDir = Join-Path $repoRoot "Data-Test\t74-$Configuration" }

$WorkDir = [IO.Path]::GetFullPath($WorkDir)
$DataDir = [IO.Path]::GetFullPath($DataDir)

$startedAt = Get-Date

# ---------- 1) 构建 ----------
if (-not $SkipBuild -and [string]::IsNullOrWhiteSpace($ExePath)) {
    Write-Host '== 1/3 构建（dotnet build） ==' -ForegroundColor Cyan

    $appProject = Join-Path $repoRoot 'src\EbookToolSuite.App\EbookToolSuite.App.csproj'
    $buildOut = & dotnet build $appProject -c $Configuration --nologo 2>&1
    $buildExit = $LASTEXITCODE

    if ($buildExit -ne 0) {
        $buildOut | ForEach-Object { Write-Host $_ }
        Write-Host "构建失败（exit=$buildExit）" -ForegroundColor Red
        exit 1
    }

    Write-Host '构建通过' -ForegroundColor Green
}
else {
    Write-Host '== 1/3 构建（跳过） ==' -ForegroundColor DarkGray
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "找不到主程序：$exe" -ForegroundColor Red
    Write-Host "先跑一次 dotnet build -c $Configuration（或传 -ExePath）" -ForegroundColor Yellow
    exit 1
}

# ---------- 准备目录 ----------
if (-not $KeepWorkDir -and (Test-Path -LiteralPath $WorkDir)) {
    Remove-Item -LiteralPath $WorkDir -Recurse -Force
}
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

if (-not $KeepDataDir -and (Test-Path -LiteralPath $DataDir)) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

# ---------- 2) 进程内冒烟 ----------
Write-Host ''
Write-Host '== 2/3 进程内冒烟（--smoke-t74） ==' -ForegroundColor Cyan
Write-Host ("工作目录：" + $WorkDir)
Write-Host ("数据目录：" + $DataDir)

# 参数必须整体作为一个带引号的字符串传，否则路径里的空格会被当成分隔符
$arguments = '--smoke-t74="' + $WorkDir + '" --datadir="' + $DataDir + '"'

# 不重定向 stdout/stderr：本机 PowerShell 5.1 下一旦用 Start-Process -RedirectStandardOutput，
# Process.ExitCode 会变成 $null（进程正常退出也读不到），无法判断验收结果。
# 冒烟自己的日志在 数据目录\Logs 里，失败时直接读日志尾部。
$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
$finished = $process.WaitForExit(900000)
if (-not $finished) {
    try { $process.Kill() } catch { }
    Write-Host '冒烟超时（900 秒）' -ForegroundColor Red
    exit 1
}

# 再调一次无参 WaitForExit 确保进程已收割、退出码可用。
$process.WaitForExit()
$smokeExit = $process.ExitCode

$reportPath = Join-Path $WorkDir 't74-report.json'
$smoke = if (Test-Path -LiteralPath $reportPath) {
    Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
else {
    $null
}

if (-not $smoke) {
    Write-Host "没有生成冒烟报告：$reportPath（退出码 $smokeExit）" -ForegroundColor Red

    $logDir = Join-Path $DataDir 'Logs'
    $latestLog = Get-ChildItem -Path $logDir -Filter 'app-*.log' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($latestLog) {
        Write-Host ("最近日志：" + $latestLog.FullName) -ForegroundColor Yellow
        Get-Content -LiteralPath $latestLog.FullName -Tail 40 | ForEach-Object { Write-Host $_ }
    }

    exit 1
}

$rows = New-Object System.Collections.Generic.List[object]
foreach ($check in $smoke.Checks) {
    $rows.Add([pscustomobject]@{
        Group  = '进程内冒烟'
        Name   = [string]$check.Name
        Ok     = [bool]$check.Ok
        Detail = [string]$check.Detail
    })
}

# ---------- 3) 合并报告 ----------
$passed = @($rows | Where-Object { $_.Ok }).Count
$total = $rows.Count
$failed = @($rows | Where-Object { -not $_.Ok })
$finishedAt = Get-Date
$allOk = ($total -gt 0) -and ($failed.Count -eq 0) -and ($smokeExit -eq 0)

$json = [pscustomobject]@{
    Task          = 'T7-4 首次运行向导与设置'
    StartedAt     = $startedAt.ToString('o')
    FinishedAt    = $finishedAt.ToString('o')
    DurationMs    = [int](($finishedAt - $startedAt).TotalMilliseconds)
    Configuration = $Configuration
    ExePath       = $exe
    WorkDir       = $WorkDir
    DataDir       = $DataDir
    Passed        = $allOk
    PassedCount   = $passed
    Total         = $total
    SmokeExit     = $smokeExit
    SmokeDurationMs = $smoke.DurationMs
    Checks        = $rows
}
$jsonPath = Join-Path $PSScriptRoot 't74-acceptance-report.json'
$json | ConvertTo-Json -Depth 6 | Out-File -LiteralPath $jsonPath -Encoding utf8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# T7-4 首次运行向导与设置 · 验收报告')
[void]$md.AppendLine('')
[void]$md.AppendLine('- 时间：' + $startedAt.ToString('yyyy-MM-dd HH:mm:ss') + ' → ' + $finishedAt.ToString('HH:mm:ss') + '（' + $json.DurationMs + ' ms）')
[void]$md.AppendLine('- 配置：' + $Configuration + '；可执行文件：`' + $exe + '`')
[void]$md.AppendLine('- 工作目录：`' + $WorkDir + '`；数据目录：`' + $DataDir + '`')
[void]$md.AppendLine('- 结果：**' + $passed + '/' + $total + ' 通过**，冒烟退出码 ' + $smokeExit)
[void]$md.AppendLine('- 命令：`dotnet build src\EbookToolSuite.App\EbookToolSuite.App.csproj -c Debug`（脚本内自动执行）→ `powershell -NoProfile -ExecutionPolicy Bypass -File build\smoke-t74.ps1`；冒烟入口 `EBookStudio.exe --smoke-t74=<WorkDir> --datadir=<DataDir>`')
[void]$md.AppendLine('- 产物：本文件 + `build\t74-acceptance-report.json`；冒烟原始报告 `Data-Test\t74-work\t74-report.json`（含逐项 Detail，日志在同目录 smoke-*.log 与 Data-Test 的 Logs/）')
[void]$md.AppendLine('')
[void]$md.AppendLine('## 逐项')
[void]$md.AppendLine('')
[void]$md.AppendLine('| 检查项 | 结果 | 说明 |')
[void]$md.AppendLine('|--------|------|------|')
foreach ($row in $rows) {
    $state = if ($row.Ok) { 'PASS' } else { 'FAIL' }
    $detail = ($row.Detail -replace '\|', '\|') -replace '[\r\n]+', ' '
    [void]$md.AppendLine('| ' + $row.Name + ' | ' + $state + ' | ' + $detail + ' |')
}
[void]$md.AppendLine('')
[void]$md.AppendLine('## 结论与完成判据覆盖')
[void]$md.AppendLine('')
[void]$md.AppendLine('- 结果：**' + $passed + '/' + $total + ' 通过**（详见上面的逐项表）。')
[void]$md.AppendLine('- 判据① 全新环境走完向导能正常用：`t74-first-run-predicate`（空目录 ShouldShow=true / 有 Config.json=false）+ `t74-first-run-wizard-flow`（默认数据目录=paths.DataRoot、选目录写指针、工具步骤可跳过、完成写 ui.firstRunCompleted、RestartRequested=true）。')
[void]$md.AppendLine('- 判据② 工具缺失时向导有明确指引：向导工具步骤实际渲染 5 个工具行，缺失项带 FixHint；冒烟断言行数>0 且可跳过（每台机器的实际安装状态只能人工看）。')
[void]$md.AppendLine('- 判据③ 迁移复制+校验+切换、全程可取消：`t74-migration-preflight` / `copy-verify` / `cancel-rollback` / `switch-existing` / `old-dir-delete` / `apppaths-pointer`。')
[void]$md.AppendLine('- 判据④ 迁移后功能正常：本冒烟不重启进程。目标目录的可用性由迁移服务的校验（文件清单、逐文件大小、数据库 integrity_check 与 user_version）与 `AppPaths.Create` 读取指针解析出新 DataRoot 来证明；真实重启验证留人工。')
[void]$md.AppendLine('- 判据⑤ 设置项全部持久化：`t74-settings-persist-all`（15 个键）+ 预加载 / 书库视图 / 封面尺寸三项专项检查。')
[void]$md.AppendLine('- 判据⑥ 书库视图双向同步：`t74-library-view-two-way`（设置页下拉→ViewModel→Config；ViewModel 切换→设置页下拉回到网格）且重启后仍从 Config 恢复。')
[void]$md.AppendLine('- **预加载页数（用户 2026-10-19 追加）**：设置页可手输 + 右侧上下箭头 ±1，范围 1–1000（越界夹取、空串回退上一次有效值），阅读器不再有工具栏入口；阅读器侧 `reader.js` 同样夹到 1–1000。PDF 页位图由宿主按 LRU 常驻最多 64 页，前端 `cacheLimit` 取 `min(preload+8, 200)` 留余量，避免把 1000 页真的占满内存。')
[void]$md.AppendLine('- **迁移排除目录**：默认不搬 Cache / Temp / WebView2Data（可重建或被运行中的进程占用）；冒烟往 Cache 放一个假文件，断言目标目录里没有它。')
[void]$md.AppendLine('- **设置键清单**：ui.language / ui.openLastBook / ui.coverSize / library.autoScan / conversion.defaultTargetFormat / conversion.maxParallel / conversion.timeoutMinutes / reader.theme / reader.typography.fontSize / reader.typography.fontFamily / reader.typography.lineHeight / reader.paginationMode / cache.limitMb / backup.limitMb / advanced.logLevel（另有 reader.pdfPreloadPages、ui.libraryView）。')
[void]$md.AppendLine('- **为什么迁移必须重启**：WebView2 的 userDataFolder 与 SQLite 连接都绑定在当前数据目录路径上，不支持热切换；迁移只负责复制、校验并写 datadir.txt 指针，下次启动从新目录开始。')
[void]$md.AppendLine('')
[void]$md.AppendLine('## 口径与证据')
[void]$md.AppendLine('')
[void]$md.AppendLine('- **设置持久化**：每项都通过设置页的真实控件写入（TextBox 触发 TextChanged、ComboBox 选 `Tag`、CheckBox 切 `IsChecked`），随后直接读 `Config.json` 文本断言键值，并用 `AppSettingsKeys` 读回。')
[void]$md.AppendLine('- **预加载页数**：`reader.pdfPreloadPages` 夹取区间 1–1000，越界与空串都回退到上一次有效值；上/下箭头 ±1，边界不越界。')
[void]$md.AppendLine('- **阅读器工具栏**：`reader.html` 不再有 `id="pdfPreload"`，`reader.js` 不再监听 `reader.pdfPreload`，`reader.css` 不再有 `#pdfPreload`。')
[void]$md.AppendLine('- **首次运行向导**：`ShouldShow` 用 `workRoot` 下临时 `AppPaths` 判定（空目录 true / 有 Config.json false）；向导的指针文件写到临时 `UserLocalRoot`，不碰真实 `%LOCALAPPDATA%`。')
[void]$md.AppendLine('- **数据目录迁移**：直接驱动 `IDataDirectoryMigrationService`。迁移目标必须放在程序目录之外（Debug 下 AppRoot 会向上解析到仓库根，因此临时目标落在系统临时目录 `%TEMP%\EBookStudio-t74-migration-*`）；每个检查结束时恢复 `datadir.txt` 指针与 `Config.json` 的 `paths.*` 键，再删临时目标。')
[void]$md.AppendLine('- **不重启进程**：迁移后的"功能正常"用 `AppPaths.Create` 解析指针 + 目标目录校验通过来证明；真实重启验证留人工（写进 DECISIONS / 计划文档）。')
[void]$md.AppendLine('- **维护动作**：清缓存 / 导出诊断 / 立即备份都只操作数据目录，检查包含文件级断言（Cache 归零、zip 条目关键字、备份目录里有 Library.db 与 Config.json）。')
[void]$md.AppendLine('')

$mdPath = Join-Path $PSScriptRoot 't74-acceptance-report.md'
$md.ToString() | Out-File -LiteralPath $mdPath -Encoding utf8

Write-Host ''
if ($allOk) {
    Write-Host ("T7-4 验收通过：{0}/{1}" -f $passed, $total) -ForegroundColor Green
    Write-Host ("报告：" + $mdPath)
    exit 0
}

Write-Host ("T7-4 验收未通过：{0}/{1}" -f $passed, $total) -ForegroundColor Red
$failed | ForEach-Object { Write-Host ("  - " + $_.Name + "：" + $_.Detail) -ForegroundColor Red }
Write-Host ("报告：" + $mdPath)
exit 2
