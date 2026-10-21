# 发布脚本（T0-8，2026-10-02 修订：保留数据目录 + 运行中检测）
#
#   powershell -File build\publish.ps1 -OutputDir publish\slim        # 精简发布（默认）
#   powershell -File build\publish.ps1 -Full -OutputDir publish\full  # 全量发布
#   powershell -File build\publish.ps1 -Full            # 全量，输出到默认 publish\
#   powershell -File build\publish.ps1 -FrameworkDependent
#   powershell -File build\publish.ps1 -Fresh           # 连数据目录和工具一起清掉（慎用）
#
# 两种发布模式（对齐计划文档 T7-6）：
#   精简发布：主程序 + JRE + EpubCheck，**不内置 Calibre**（省 634 MB）。
#             Calibre 需下载便携版解压到 Tools\Calibre Portable\（程序按 manifest 的
#             固定相对路径找它，目前没有「指向外部安装」的设置项）；没有 Calibre 时
#             转换功能不可用（启动后顶部红条说明原因），其余功能正常。
#             体积约 400-500 MB。
#   全量发布：再带上 Tools\Calibre Portable（634 MB），开箱即用、可拷 U 盘换机。
#             体积约 1.0-1.2 GB。
#
# 两个发布包都自带 README.md（仓库根目录那份），README 里写了启动、数据目录、
# 两种模式的差别，以及精简模式缺的 Calibre / WebView2 运行时去哪下载。
#
# 2026-10-13 修订：输出目录里的 Data\（书库）与 Tools\（已打包的工具）都会先挪开、发布完原样放回。
#   所以「用精简模式重发一次」不会再削掉上一次全量发布带的 Calibre（那次事故丢的正是这 794 MB 工具）；
#   真要清掉它们，用 -Fresh。
#
# 注意：本文件必须保持 UTF-8 带 BOM，否则 PowerShell 5.1 按 ANSI 读会乱码。

param(
    [switch]$Full,
    [switch]$FrameworkDependent,
    [string]$OutputDir,

    # 默认会保留输出目录里的 Data\（那是书库）。加这个开关才连它一起清掉。
    [switch]$Fresh
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$out  = if ($OutputDir) { $OutputDir } else { Join-Path $root 'publish' }
$project = Join-Path $root 'src\EbookToolSuite.App\EbookToolSuite.App.csproj'

if (-not (Test-Path $project)) {
    Write-Error "找不到 $project"
    exit 1
}

$out = [IO.Path]::GetFullPath($out)

Write-Host "=== EBookStudio 发布 ===" -ForegroundColor Cyan
Write-Host ("模式: {0}" -f $(if ($Full) { '全量便携' } else { '精简' }))
Write-Host ("运行时: {0}" -f $(if ($FrameworkDependent) { '依赖 .NET 10 运行时' } else { '自包含（自带运行时）' }))
Write-Host "输出: $out"
Write-Host ''

# ---------- 前置检查：发布目录里的程序不能在运行 ----------
#
# 不检查的话，Remove-Item 会在被占用的日志文件上失败，
# 报出来的是一句很难懂的 IOException（2026-10-02 实际踩到过）。

$running = @(Get-Process -Name 'EBookStudio' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($out, [StringComparison]::OrdinalIgnoreCase) })

if ($running.Count -gt 0) {
    Write-Host '发布目录里的程序正在运行，文件被占用，无法覆盖。' -ForegroundColor Red
    foreach ($p in $running) {
        Write-Host ("  PID {0}  {1}" -f $p.Id, $p.Path) -ForegroundColor Red
    }
    Write-Host '请先关闭它，再重跑本脚本。' -ForegroundColor Yellow
    exit 1
}

# ---------- 数据目录：先挪走，发布完再挪回来 ----------
#
# 默认数据目录就是「程序目录下的 Data\」（绿色便携形态），里面装着整个书库。
# 发布脚本**绝不能**把它当垃圾清掉。做法是先挪到同级临时目录，清空输出后再挪回来；
# 用 try/finally 保证即使发布失败也会还回去。

$outData   = Join-Path $out 'Data'
$stashData = $null
$restore   = $false

if ((Test-Path $outData) -and -not $Fresh) {
    $stashData = Join-Path (Split-Path $out -Parent) ('.publish-data-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    Move-Item $outData $stashData
    $restore = $true
    Write-Host "已暂时移开数据目录（书库）：$outData" -ForegroundColor Yellow
}
elseif ($Fresh) {
    Write-Host '-Fresh：会连数据目录一起清掉。' -ForegroundColor Yellow
    Write-Host '-Fresh：会连工具目录一起清掉。' -ForegroundColor Yellow
}

# ---------- 工具目录：同样先挪走，发布完再挪回来 ----------
#
# 2026-10-13 修订：Calibre Portable(634 MB) + JRE(125 MB) + EpubCheck(35 MB) 是上一次
# 「-Full 全量发布」的成果。原脚本只保护 Data\，于是默认的精简模式重发会把它们连根削掉
# （只剩一个 manifest.json，程序一启动就报「缺少必需工具」）。这里跟 Data\ 一样对待。

$outTools     = Join-Path $out 'Tools'
$stashTools   = $null
$restoreTools = $false

if ((Test-Path $outTools) -and -not $Fresh) {
    $stashTools = Join-Path (Split-Path $out -Parent) ('.publish-tools-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    Move-Item $outTools $stashTools
    $restoreTools = $true

    $toolsMb = (Get-ChildItem $stashTools -Recurse -File -ErrorAction SilentlyContinue |
                Measure-Object Length -Sum).Sum / 1MB

    Write-Host ("已暂时移开工具目录（{0:N1} MB）：{1}" -f $toolsMb, $outTools) -ForegroundColor Yellow
}
$dotnetArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $out,
    '--nologo',

    # 关键：单文件发布与 SQLite 的 e_sqlite3、WebView2Loader 两个原生库冲突，
    # 开了之后运行期才炸，且错误信息极难定位。
    '-p:PublishSingleFile=false',

    # WPF 不支持 trimming，SQLite / WebView2 的原生库也会被裁掉。
    '-p:PublishTrimmed=false',

    # WPF 不支持 AOT。
    '-p:PublishAot=false',

    '-p:DebugType=embedded',
    '-p:SatelliteResourceLanguages=zh-Hans'
)

if ($FrameworkDependent) {
    $dotnetArgs += '-p:SelfContained=false'
    $dotnetArgs += '-p:PublishReadyToRun=false'
} else {
    $dotnetArgs += '-p:SelfContained=true'
    $dotnetArgs += '-p:PublishReadyToRun=true'
}

try {
    if (Test-Path $out) {
        Write-Host '清空输出目录...'
        Remove-Item $out -Recurse -Force
    }

    Write-Host "执行: dotnet $($dotnetArgs -join ' ')"
    Write-Host ''

    dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出码 $LASTEXITCODE"
    }

    # ---------- 先把上一次的工具目录放回 ----------
    #
    # 顺序是关键：必须在下面「工具清单」之前放回。否则精简模式重发时，
    # 上面的 Remove-Item 已经把工具删了，这里写回的就只剩一个 manifest.json。

    if ($restoreTools -and $stashTools -and (Test-Path $stashTools)) {
        Move-Item $stashTools $outTools
        $restoreTools = $false
        Write-Host "工具目录已还原：$outTools" -ForegroundColor Yellow
    }
    # ---------- 工具清单 ----------

    $toolsSrc = Join-Path $root 'Tools'
    $toolsDst = Join-Path $out 'Tools'
    New-Item -ItemType Directory -Path $toolsDst -Force | Out-Null

    # 清单一定要带，ToolLocator 靠它认识各工具。
    Copy-Item (Join-Path $toolsSrc 'manifest.json') $toolsDst -Force

    if ($Full) {
        foreach ($tool in @('Calibre Portable', 'JRE', 'EpubCheck')) {
            $src = Join-Path $toolsSrc $tool
            if (-not (Test-Path $src)) {
                Write-Warning "全量模式下缺少 Tools\$tool，已跳过。"
                continue
            }

            $dst = Join-Path $toolsDst $tool
            Write-Host "拷贝 Tools\$tool ..."
            # 目标已存在（上一次发布留下的）就先删再拷：往已存在的目录里 -Recurse 拷贝会
            # 变成拷进子目录，得到 Tools\X\X\... 这种嵌套，不如显式清一下再拷。
            if (Test-Path $dst) {
                Remove-Item $dst -Recurse -Force
            }

            Copy-Item $src $dst -Recurse -Force
        }
    } else {
        # 精简模式：JRE + EpubCheck 照拷（EPUB 校验可用），Calibre 不拷（省 634 MB）。
        # 与全量模式共用「先删目标再拷」的做法，避免重复发布时出现 Tools\JRE\JRE\... 嵌套。
        foreach ($tool in @('JRE', 'EpubCheck')) {
            $src = Join-Path $toolsSrc $tool
            if (-not (Test-Path $src)) {
                Write-Warning "精简模式下缺少 Tools\$tool，已跳过（对应功能不可用）。"
                continue
            }

            $dst = Join-Path $toolsDst $tool
            if (Test-Path $dst) {
                Remove-Item $dst -Recurse -Force
            }

            Write-Host "拷贝 Tools\$tool ..."
            Copy-Item $src $dst -Recurse -Force
        }

        $calibreDst = Join-Path $toolsDst 'Calibre Portable'
        if (Test-Path $calibreDst) {
            Write-Host '精简模式：不拷贝 Calibre；输出目录里原有的一份（上次全量发布留下的）仍保留。' -ForegroundColor Yellow
        }
        else {
            Write-Host '精简模式：本包不含 Calibre（转换功能不可用，其余功能正常）。' -ForegroundColor Yellow
            Write-Host '补装：下载 Calibre 便携版解压到 Tools\Calibre Portable\（下载地址见 README.md）。' -ForegroundColor Yellow
            Write-Host '     程序按 Tools\manifest.json 的固定相对路径查找，没有「指向外部安装」的设置项。' -ForegroundColor Yellow
            Write-Host '想要开箱即用：跑一次 build\publish.ps1 -Full -OutputDir publish\full（约 +634 MB）。' -ForegroundColor Yellow
        }
    }

    # ---------- 附带文档 ----------

    # T7-6：发布包必须自带 README（怎么启动、数据在哪、精简模式缺什么工具、去哪下载）。
    $readmeSrc = Join-Path $root 'README.md'
    if (Test-Path $readmeSrc) {
        Copy-Item $readmeSrc (Join-Path $out 'README.md') -Force
    }
    else {
        Write-Warning '仓库根目录没有 README.md，发布包里将没有说明文档。'
    }

    # 注意：仓库 docs\ 是**内部开发基线**（本机路径、真实样本书名），一律不进发布包。
    # 发布包里只保留 README.md 这一份面向用户的说明。
}
finally {
    if ($restoreTools -and $stashTools -and (Test-Path $stashTools)) {
        Move-Item $stashTools $outTools
        $restoreTools = $false
        Write-Host "工具目录已还原：$outTools" -ForegroundColor Yellow
    }
    if ($restore -and $stashData -and (Test-Path $stashData)) {
        Move-Item $stashData $outData
        Write-Host ''
        Write-Host "数据目录（书库）已还原：$outData" -ForegroundColor Yellow
    }
}

# ---------- 体积统计 ----------

Write-Host ''
Write-Host '=== 结果 ===' -ForegroundColor Cyan

$files = Get-ChildItem $out -Recurse -File
$total = ($files | Measure-Object Length -Sum).Sum
Write-Host ("文件数 {0}，总体积 {1:N1} MB" -f $files.Count, ($total / 1MB))

Write-Host ''
Write-Host '各目录占用:'
Get-ChildItem $out -Directory | ForEach-Object {
    $size = (Get-ChildItem $_.FullName -Recurse -File |
             Measure-Object Length -Sum).Sum
    Write-Host ("  {0,-20} {1,10:N1} MB" -f $_.Name, ($size / 1MB))
}

$exe = Join-Path $out 'EBookStudio.exe'
Write-Host ''
if (Test-Path $exe) {
    Write-Host "可执行文件: $exe" -ForegroundColor Green
} else {
    Write-Error "输出目录里没有 EBookStudio.exe，发布不完整。"
}

Write-Host ''
Write-Host '发布完成。' -ForegroundColor Green
exit 0