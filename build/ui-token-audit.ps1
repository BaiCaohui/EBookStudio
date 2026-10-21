# T7-3 静态界面令牌审计：把"看起来不统一"的地方变成可判定的数字。
# 用法：powershell -File build\ui-token-audit.ps1 [-RepoRoot <仓库根>] [-Json <输出 json 路径>]
# 退出码：0 = 全部通过；2 = 有违规；1 = 脚本自身跑不起来。

[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Json
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$appRoot = Join-Path $RepoRoot 'src\EbookToolSuite.App'
$readerAssets = Join-Path $RepoRoot 'src\EbookToolSuite.Reader\Assets\Reader'

$failures = New-Object System.Collections.Generic.List[string]
$checks = New-Object System.Collections.Generic.List[object]

function Add-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    $script:checks.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Detail = $Detail })
    if (-not $Ok) { $script:failures.Add("$Name：$Detail") }
}

$themeFiles = @(
    (Join-Path $appRoot 'Themes\Colors.xaml'),
    (Join-Path $appRoot 'Themes\Dark.xaml'),
    (Join-Path $appRoot 'Themes\Green.xaml'),
    (Join-Path $appRoot 'Themes\Sepia.xaml')
)

$allXaml = Get-ChildItem -Path $appRoot -Recurse -Filter *.xaml | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

# ---------- 1) 主题文件之外不许有硬编码颜色 ----------
$hardcoded = @()
foreach ($file in $allXaml) {
    if ($themeFiles -contains $file.FullName) { continue }
    $line = 0
    foreach ($text in (Get-Content -LiteralPath $file.FullName -Encoding UTF8)) {
        $line++
        if ($text -match '#[0-9A-Fa-f]{6,8}\b') {
            $hardcoded += ('{0}:{1}' -f $file.FullName.Substring($RepoRoot.Length + 1), $line)
        }
    }
}
Add-Check 'xaml-hardcoded-colors' ($hardcoded.Count -eq 0) ('' + $hardcoded.Count + ' 处（' + ($hardcoded -join '、') + '）')

# ---------- 2) 页面里不许再有字形字面量（集中到 Themes\Icons.xaml） ----------
$glyphs = @()
foreach ($file in $allXaml) {
    if ($file.Name -eq 'Icons.xaml') { continue }
    $line = 0
    foreach ($text in (Get-Content -LiteralPath $file.FullName -Encoding UTF8)) {
        $line++
        if ($text -match '&#x[0-9A-Fa-f]{4};') {
            $glyphs += ('{0}:{1}' -f $file.FullName.Substring($RepoRoot.Length + 1), $line)
        }
    }
}
Add-Check 'xaml-glyph-literals' ($glyphs.Count -eq 0) ('' + $glyphs.Count + ' 处（' + ($glyphs -join '、') + '）')

# ---------- 3) 不许再用系统消息框 ----------
$messageBoxes = @()
foreach ($file in (Get-ChildItem -Path $appRoot -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
    $line = 0
    foreach ($text in (Get-Content -LiteralPath $file.FullName -Encoding UTF8)) {
        $line++
        if ($text -match 'MessageBox\.Show') {
            $messageBoxes += ('{0}:{1}' -f $file.FullName.Substring($RepoRoot.Length + 1), $line)
        }
    }
}
Add-Check 'no-messagebox' ($messageBoxes.Count -eq 0) ('' + $messageBoxes.Count + ' 处（' + ($messageBoxes -join '、') + '）')

# ---------- 4) 字号必须在八档之内 ----------
$allowedFontSizes = @('11', '12', '13', '14', '16', '20', '34', '40')
$badSizes = @()
foreach ($file in $allXaml) {
    $line = 0
    foreach ($text in (Get-Content -LiteralPath $file.FullName -Encoding UTF8)) {
        $line++
        $sizeMatches = [regex]::Matches($text, 'FontSize="([0-9.]+)"')
        foreach ($match in $sizeMatches) {
            if ($allowedFontSizes -notcontains $match.Groups[1].Value) {
                $badSizes += ('{0}:{1} FontSize={2}' -f $file.FullName.Substring($RepoRoot.Length + 1), $line, $match.Groups[1].Value)
            }
        }
    }
}
Add-Check 'font-size-scale' ($badSizes.Count -eq 0) ('' + $badSizes.Count + ' 处越界（' + ($badSizes -join '、') + '）')

# ---------- 5) 圆角必须在四档之内 ----------
$allowedRadii = @('4', '6', '8', '10')
$badRadii = @()
foreach ($file in $allXaml) {
    $line = 0
    foreach ($text in (Get-Content -LiteralPath $file.FullName -Encoding UTF8)) {
        $line++
        $radiusMatches = [regex]::Matches($text, 'CornerRadius="([0-9]+)"')
        foreach ($match in $sizeMatches) {
            if ($allowedRadii -notcontains $match.Groups[1].Value) {
                $badRadii += ('{0}:{1} CornerRadius={2}' -f $file.FullName.Substring($RepoRoot.Length + 1), $line, $match.Groups[1].Value)
            }
        }
    }
}
Add-Check 'corner-radius-scale' ($badRadii.Count -eq 0) ('' + $badRadii.Count + ' 处越界（' + ($badRadii -join '、') + '）')

# ---------- 6) 各份主题字典的 key 必须完全一致 ----------
$keySets = @{}
foreach ($theme in $themeFiles) {
    $keys = [regex]::Matches((Get-Content -LiteralPath $theme -Raw -Encoding UTF8), 'x:Key="([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    $keySets[(Split-Path $theme -Leaf)] = $keys
}
$baseline = @($keySets['Colors.xaml'])
$parityOk = $true
$parityDetail = ''
foreach ($pair in $keySets.GetEnumerator()) {
    $diff = Compare-Object -ReferenceObject $baseline -DifferenceObject @($pair.Value)
    if ($diff) {
        $parityOk = $false
        $parityDetail += ('{0} 差异 {1} 项；' -f $pair.Key, $diff.Count)
    }
}
if (-not $parityDetail) { $parityDetail = '四份字典 key 完全一致（' + $baseline.Count + ' 个）' }
Add-Check 'theme-key-parity' $parityOk $parityDetail

# ---------- 7) 应用图标 ----------
$csproj = Join-Path $appRoot 'EbookToolSuite.App.csproj'
$iconPath = Join-Path $appRoot 'Assets\app.ico'
$iconOk = (Test-Path -LiteralPath $iconPath) -and ((Get-Content -LiteralPath $csproj -Raw -Encoding UTF8) -match 'ApplicationIcon')
Add-Check 'application-icon' $iconOk ('app.ico=' + (Test-Path -LiteralPath $iconPath) + '，csproj 配置=' + ((Get-Content -LiteralPath $csproj -Raw -Encoding UTF8) -match 'ApplicationIcon'))

# ---------- 8) 窗口统一走主题（代码里 ApplyWindowChrome 存在） ----------
$themeManagerPath = Join-Path $appRoot 'Themes\ThemeManager.cs'
$chromeOk = (Test-Path -LiteralPath $themeManagerPath) -and
    ((Get-Content -LiteralPath $themeManagerPath -Raw -Encoding UTF8) -match 'ApplyWindowChrome')
Add-Check 'window-theme-chrome' $chromeOk '所有窗口在 Loaded 时统一套背景/前景/字号/标题栏'

# ---------- 9) 隐式控件词表存在 ----------
$controlsPath = Join-Path $appRoot 'Themes\Controls.xaml'
$controlsText = Get-Content -LiteralPath $controlsPath -Raw -Encoding UTF8
$requiredStyles = @(
    'TargetType="Button"', 'TargetType="TextBox"', 'TargetType="ComboBox"', 'TargetType="CheckBox"',
    'TargetType="RadioButton"', 'TargetType="ProgressBar"', 'TargetType="ToolTip"', 'TargetType="ScrollBar"',
    'TargetType="ListView"', 'TargetType="ListViewItem"', 'TargetType="GridViewColumnHeader"',
    'TargetType="Expander"', 'TargetType="GridSplitter"', 'TargetType="Menu"', 'TargetType="MenuItem"',
    'TargetType="ContextMenu"', 'TargetType="Separator"', 'TargetType="Window"'
)
$missingStyles = @($requiredStyles | Where-Object { $controlsText -notmatch [regex]::Escape($_) })
Add-Check 'control-vocabulary' ($missingStyles.Count -eq 0) ('缺：' + ($missingStyles -join '、'))

# ---------- 10) 阅读器接线 ----------
$readerJs = Join-Path $readerAssets 'reader.js'
$readerOk = $false
$readerDetail = '找不到 reader.js'
if (Test-Path -LiteralPath $readerJs) {
    $readerText = Get-Content -LiteralPath $readerJs -Raw -Encoding UTF8
    $hasVars = ($readerText -match '--shell-border-strong') -and ($readerText -match '--shell-icon-fg')
    $hasInherit = ($readerText -match 'URLSearchParams\(location\.search\)')
    $readerOk = $hasVars -and $hasInherit
    $readerDetail = ('漏设变量补齐={0}，首帧主题参数={1}' -f $hasVars, $hasInherit)
}
Add-Check 'reader-theme-wiring' $readerOk $readerDetail

# ---------- 11) 拖动导入：整窗蒙版 + 格式过滤 ----------
$mainXaml = Join-Path $appRoot 'MainWindow.xaml'
$mainCode = Join-Path $appRoot 'MainWindow.xaml.cs'
$dragOk = (Test-Path -LiteralPath $mainXaml) -and (Test-Path -LiteralPath $mainCode)
$dragDetail = '缺 MainWindow.xaml / MainWindow.xaml.cs'
if ($dragOk) {
    $xamlText = Get-Content -LiteralPath $mainXaml -Raw -Encoding UTF8
    $codeText = Get-Content -LiteralPath $mainCode -Raw -Encoding UTF8
    $hasOverlay = ($xamlText -match 'x:Name="DropOverlay"') -and ($xamlText -match 'AllowDrop="True"')
    # 与文件选择器共用同一条导入管线：窗口落点调 RunPathsAsync，而不是自己筛格式/弹窗
    $hasSharedPipeline = ($codeText -match 'OnWindowDrop') -and ($codeText -match 'RunPathsAsync')
    $dragOk = $hasOverlay -and $hasSharedPipeline
    $dragDetail = ('整窗蒙版={0}，复用导入管线/落点处理={1}' -f $hasOverlay, $hasSharedPipeline)
}
Add-Check 'drag-drop-overlay' $dragOk $dragDetail
# ---------- 12) 编辑窗口必须跟书库主题（用户 2026-10-21：编辑书籍左栏 / 可视编辑 / TXT 全文编辑曾经不跟主题） ----------
# 书库主题 = ui.theme（六档含自定义），不是阅读器主题 reader.theme；两套互不影响。
$editorAssets = Join-Path (Split-Path -Parent $readerAssets) 'Editor'
$bookEditorXaml = Join-Path $appRoot 'Views\BookEditorWindow.xaml'
$txtEditorCode = Join-Path $appRoot 'Views\TxtEditorWindow.xaml.cs'
$sourceEditorCode = Join-Path $appRoot 'Views\ChapterSourceWindow.xaml.cs'
$visualEditorCode = Join-Path $appRoot 'Views\ChapterVisualEditorWindow.xaml.cs'
$editorCssPath = Join-Path $editorAssets 'editor.css'
$editorJsPath = Join-Path $editorAssets 'editor.js'
$themeManagerText = Get-Content -LiteralPath $themeManagerPath -Raw -Encoding UTF8

function Test-Contains([string]$Path, [string]$Pattern) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    return ((Get-Content -LiteralPath $Path -Raw -Encoding UTF8) -match $Pattern)
}

$bookEditorOk = (Test-Contains $bookEditorXaml 'ChapterListStyle') -and (Test-Contains $bookEditorXaml 'LayerBackgroundBrush')
$txtEditorOk = Test-Contains $txtEditorCode 'AvalonTheme\.Apply\(Editor\)'
$sourceEditorOk = Test-Contains $sourceEditorCode 'AvalonTheme\.Apply\(Editor\)'
$visualEditorOk = (Test-Contains $visualEditorCode 'themeJson') -and (Test-Contains $visualEditorCode 'etsApplyTheme')
$editorCssOk = (Test-Contains $editorCssPath '--ets-host') -and (Test-Contains $editorCssPath 'var\(--ets-')
$editorJsOk = (Test-Contains $editorJsPath 'etsApplyTheme') -and (Test-Contains $editorJsPath 'refreshThemeLayer')
$customBorderOk = $themeManagerText -match 'DeriveCustomBorder'
Add-Check 'editor-theme-wiring' `
    ($bookEditorOk -and $txtEditorOk -and $sourceEditorOk -and $visualEditorOk -and $editorCssOk -and $editorJsOk -and $customBorderOk) `
    ('书籍编辑栏主题=' + $bookEditorOk + '，TXT 编辑区=' + $txtEditorOk + '，源码编辑区=' + $sourceEditorOk +
        '，可视编辑页主题参数=' + $visualEditorOk + '，editor.css 走变量=' + $editorCssOk +
        '，editor.js 主题层=' + $editorJsOk + '，自定义描边推导=' + $customBorderOk)

# ---------- 输出 ----------
$okCount = ($checks | Where-Object { $_.Ok }).Count
Write-Output ('T7-3 静态界面审计：{0}/{1} 通过' -f $okCount, $checks.Count)
foreach ($check in $checks) {
    $state = if ($check.Ok) { 'OK  ' } else { 'FAIL' }
    Write-Output ('  [{0}] {1,-24} {2}' -f $state, $check.Name, $check.Detail)
}

if ($Json) {
    $payload = [pscustomobject]@{
        StartedAt = (Get-Date).ToString('o')
        Passed = ($failures.Count -eq 0)
        PassedCount = $okCount
        Total = $checks.Count
        Checks = $checks
    }
    $payload | ConvertTo-Json -Depth 5 | Out-File -LiteralPath $Json -Encoding UTF8
    Write-Output ('审计结果已写入：' + $Json)
}

if ($failures.Count -gt 0) {
    Write-Output ''
    Write-Output ('违规 {0} 项：' -f $failures.Count)
    $failures | ForEach-Object { Write-Output ('  - ' + $_) }
    exit 2
}

exit 0
