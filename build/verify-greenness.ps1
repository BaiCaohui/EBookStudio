# 绿色化验证（T0-8）
#
#   powershell -File build\verify-greenness.ps1
#   powershell -File build\verify-greenness.ps1 -ProgramDir publish -DataDir D:\EBookData
#
# 原理：在跑程序之前/之后各拍一次快照，比对"允许写入区域之外"有没有新增或修改。
#
# 为什么不用 Process Monitor 自动化：ProcMon 需要管理员权限加载驱动，
# 无法在无人值守的脚本里跑。ProcMon 作为"深度排查手段"保留，做法见
# docs/ 下的说明。本脚本负责把回归卡住  它能抓住真正会复发的那类问题
# （例如 Calibre 的 CALIBRE_* 环境变量没生效，去写 %LOCALAPPDATA%）。
#
# 注意：本文件必须保持 UTF-8 带 BOM。

param(
    [string]$ProgramDir,
    [string]$DataDir,
    [switch]$SkipSmoke
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
if (-not $ProgramDir) { $ProgramDir = Join-Path $root 'publish' }
$ProgramDir = (Resolve-Path $ProgramDir).Path
$exe = Join-Path $ProgramDir 'EBookStudio.exe'

if (-not (Test-Path $exe)) {
    Write-Error "找不到 $exe。先跑 build\publish.ps1。"
    exit 1
}

if (-not $DataDir) { $DataDir = Join-Path $ProgramDir 'Data' }
$DataDir = [IO.Path]::GetFullPath($DataDir)

$script:problems = New-Object System.Collections.Generic.List[string]

function Get-TopLevelNames([string]$path) {
    if (-not (Test-Path $path)) { return @() }
    return @(Get-ChildItem $path -Directory -ErrorAction SilentlyContinue |
             Select-Object -ExpandProperty Name)
}

function Get-FileSnapshot([string]$pathFor, [string]$exclude) {
    $map = @{}
    if (-not (Test-Path $pathFor)) { return $map }
    Get-ChildItem $pathFor -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
        if ($exclude -and $_.FullName.StartsWith($exclude, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        $map[$_.FullName] = $_.LastWriteTimeUtc.Ticks
    }
    return $map
}

function Compare-Snapshot($before, $after, [string]$label) {
    foreach ($key in $after.Keys) {
        if (-not $before.ContainsKey($key)) {
            $script:problems.Add("[$label] 新增文件: $key")
        } elseif ($before[$key] -ne $after[$key]) {
            $script:problems.Add("[$label] 被修改: $key")
        }
    }
}

function Compare-Names($before, $after, [string]$label, [string]$filter) {
    foreach ($name in $after) {
        if ($before -contains $name) { continue }
        if ($filter -and $name -notlike $filter) { continue }
        $script:problems.Add("[$label] 新增目录: $name")
    }
}

Write-Host '=== 绿色化验证 ===' -ForegroundColor Cyan
Write-Host "程序目录: $ProgramDir"
Write-Host "数据目录: $DataDir"
Write-Host ''

$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$appData      = [Environment]::GetFolderPath('ApplicationData')
$tempDir      = [Environment]::GetEnvironmentVariable('TEMP')

Write-Host '拍快照（前）...'
$before = @{
    LocalTop   = Get-TopLevelNames $localAppData
    RoamingTop = Get-TopLevelNames $appData
    TempTop    = Get-TopLevelNames $tempDir
    Program    = Get-FileSnapshot $ProgramDir $DataDir
}

# ---------- 跑程序 ----------
if (-not $SkipSmoke) {
    Write-Host '运行冒烟测试（会弹一下 WebView2 窗口）...'

    $cases = @(
        @{ name = 'webview'; arg = '--smoke-webview' },
        @{ name = 'logging'; arg = '--smoke-logging' }
    )

    foreach ($case in $cases) {
        $argLine = $case.arg + ' --datadir="' + $DataDir + '"'
        $proc = Start-Process -FilePath $exe -ArgumentList $argLine -PassThru
        if (-not $proc.WaitForExit(180000)) {
            $proc.Kill()
            $script:problems.Add("[smoke:$($case.name)] 超过 180 秒未退出")
            continue
        }

        if ($proc.ExitCode -ne 0) {
            $script:problems.Add("[$($case.name)] 冒烟测试 exit=$($proc.ExitCode)")
        } else {
            Write-Host ("  {0,-10} exit=0" -f $case.name) -ForegroundColor Gray
        }
    }
}

Write-Host '拍快照（后）...'
$after = @{
    LocalTop   = Get-TopLevelNames $localAppData
    RoamingTop = Get-TopLevelNames $appData
    TempTop    = Get-TopLevelNames $tempDir
    Program    = Get-FileSnapshot $ProgramDir $DataDir
}

# ---------- 比对 ----------
Compare-Names $before.LocalTop   $after.LocalTop   'LOCALAPPDATA' '*'
Compare-Names $before.RoamingTop $after.RoamingTop 'APPDATA'      '*'
Compare-Names $before.TempTop    $after.TempTop    'TEMP'         'calibre*'
Compare-Snapshot $before.Program $after.Program    '程序目录'

# Calibre 专属检查：这两个是"CALIBRE_* 环境变量没生效"的典型症状。
# 注意判据是「本次测试**新产生**」而不是「存在」 机器上可能本来就装着
# 系统版 Calibre，它的残留目录不是本程序的问题。
foreach ($suspect in @('calibre-cache', 'Calibre')) {
    $p = Join-Path $localAppData $suspect
    if ((Test-Path $p) -and ($before.LocalTop -notcontains $suspect)) {
        $script:problems.Add("[LOCALAPPDATA] 本次新增 Calibre 目录: $p")
    }
}

if ((Test-Path (Join-Path $appData 'calibre')) -and ($before.RoamingTop -notcontains 'calibre')) {
    $script:problems.Add("[APPDATA] 本次新增 Calibre 目录: " + (Join-Path $appData 'calibre'))
}

# ---------- 结论 ----------
Write-Host ''
if ($script:problems.Count -eq 0) {
    Write-Host '通过：允许写入区域之外零新增、零修改。' -ForegroundColor Green
    exit 0
}

Write-Host ("发现 {0} 个问题：" -f $script:problems.Count) -ForegroundColor Red
foreach ($p in $script:problems) {
    Write-Host ("  " + $p) -ForegroundColor Red
}

Write-Host ''
Write-Host '允许写入的区域只有：' -ForegroundColor Yellow
Write-Host "  $DataDir"
Write-Host '  %TEMP% 下 WebView2 自身的临时文件'
exit 2