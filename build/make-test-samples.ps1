<#
.SYNOPSIS
    T7-5 全量回归：可构造测试样本生成器（Windows PowerShell 5.1 薄包装）。

.DESCRIPTION
    本脚本只做三件事：
      1. 找到一个可用的 Python 3 解释器（优先 $env:EBOOK_TOOLSUITE_PYTHON）；
      2. 把参数原样转交给 build\t75_make_samples.py；
      3. 回传退出码（0 成功，1 失败）。

    真正的生成逻辑、manifest 与自检全部在 t75_make_samples.py 里。
    样本来自 build\t75-samples.catalog.json，共 40 个样本。

.PARAMETER OutputDir
    输出目录，默认 Data-Test\t75-corpus。相对路径相对调用者当前目录。

.PARAMETER Only
    逗号分隔的样本 id，例如 "A01,B01"。给了就只造这些；
    C 组需要 A01 作源时会自动补造 A01。

.PARAMETER SkipCalibre
    跳过 C 组两个样本（没有 Calibre 的环境）。manifest 里标 skipped，
    但不会伪造文件。

.PARAMETER Force
    输出目录已存在时先清空再重建。不加时，若目录存在且 manifest 完整，
    打印「已存在，跳过」并返回 0。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build\make-test-samples.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build\make-test-samples.ps1 -Only "A01,B01" -Force

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build\make-test-samples.ps1 -SkipCalibre
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$OutputDir = 'Data-Test\t75-corpus',

    [string]$Only = '',

    [switch]$SkipCalibre,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Write-Info {
    param([string]$Message)
    Write-Host $Message
}

function Write-Fail {
    param([string]$Message)
    [Console]::Error.WriteLine($Message)
}

# 运行 <exe> --version，确认它确实是 Python 3（排除 Windows 应用商店的占位程序）。
function Test-PythonExe {
    param(
        [string]$Exe,
        [string[]]$PrefixArgs = @()
    )

    if ([string]::IsNullOrWhiteSpace($Exe)) { return $false }
    if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { return $false }

    try {
        $output = & $Exe @PrefixArgs --version 2>&1
        $text = ($output | Out-String)
        return ($text -match 'Python\s+3\.')
    }
    catch {
        return $false
    }
}

# 依次尝试：环境变量 -> PATH 里的 python -> 常见安装路径 -> py 启动器。
function Find-Python {
    $candidates = New-Object System.Collections.ArrayList

    if (-not [string]::IsNullOrWhiteSpace($env:EBOOK_TOOLSUITE_PYTHON)) {
        [void]$candidates.Add([pscustomobject]@{
            Exe    = $env:EBOOK_TOOLSUITE_PYTHON
            Args   = @()
            Source = '环境变量 EBOOK_TOOLSUITE_PYTHON'
        })
    }

    $pathCommand = Get-Command python -ErrorAction SilentlyContinue
    if ($null -ne $pathCommand -and -not [string]::IsNullOrWhiteSpace($pathCommand.Source)) {
        [void]$candidates.Add([pscustomobject]@{
            Exe    = $pathCommand.Source
            Args   = @()
            Source = 'PATH 中的 python'
        })
    }

    $patterns = @()
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $patterns += (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python*\python.exe')
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $patterns += (Join-Path $env:ProgramFiles 'Python*\python.exe')
    }
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $patterns += (Join-Path $programFilesX86 'Python*\python.exe')
    }
    $patterns += 'C:\Python*\python.exe'

    foreach ($pattern in $patterns) {
        $found = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue |
            Sort-Object -Property FullName -Descending
        foreach ($item in $found) {
            [void]$candidates.Add([pscustomobject]@{
                Exe    = $item.FullName
                Args   = @()
                Source = '常见安装路径'
            })
        }
    }

    $pyCommand = Get-Command py -ErrorAction SilentlyContinue
    if ($null -ne $pyCommand -and -not [string]::IsNullOrWhiteSpace($pyCommand.Source)) {
        [void]$candidates.Add([pscustomobject]@{
            Exe    = $pyCommand.Source
            Args   = @('-3')
            Source = 'py 启动器（py -3）'
        })
    }

    foreach ($candidate in $candidates) {
        if (Test-PythonExe -Exe $candidate.Exe -PrefixArgs $candidate.Args) {
            return $candidate
        }
    }
    return $null
}

$python = Find-Python
if ($null -eq $python) {
    Write-Fail '错误：没有找到可用的 Python 3 解释器。'
    Write-Fail '请安装 Python 3.8 或更高版本（并安装 Pillow），或者设置环境变量指向 python.exe：'
    Write-Fail '    $env:EBOOK_TOOLSUITE_PYTHON = ''C:\Python311\python.exe'''
    Write-Fail '然后再运行本脚本。'
    exit 1
}

# Python 以 UTF-8 输出，PowerShell 也按 UTF-8 解码，避免中文变乱码。
$env:PYTHONIOENCODING = 'utf-8'
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
}
catch {
    # 老控制台不允许改代码页时忽略；Python 仍按 PYTHONIOENCODING 输出。
}

Write-Info ('使用 Python：' + $python.Exe + '（来源：' + $python.Source + '）')

$scriptPath = Join-Path $PSScriptRoot 't75_make_samples.py'
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    Write-Fail ('错误：找不到生成器脚本：' + $scriptPath)
    exit 1
}

$arguments = New-Object System.Collections.Generic.List[string]
$arguments.Add($scriptPath)
$arguments.Add('--output-dir')
$arguments.Add($OutputDir)
if (-not [string]::IsNullOrWhiteSpace($Only)) {
    $arguments.Add('--only')
    $arguments.Add($Only)
}
if ($SkipCalibre) {
    $arguments.Add('--skip-calibre')
}
if ($Force) {
    $arguments.Add('--force')
}

$allArguments = @($python.Args) + $arguments.ToArray()
& $python.Exe @allArguments
$exitCode = $LASTEXITCODE
if ($null -eq $exitCode) { $exitCode = 0 }

if ($exitCode -ne 0) {
    Write-Fail ('生成器失败，退出码 ' + $exitCode + '。')
    exit 1
}
exit 0
