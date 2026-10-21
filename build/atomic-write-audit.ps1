<#
.SYNOPSIS
    T7-2 判据 4 静态审查：所有写文件操作必须是「临时文件 + 原子替换」。

.DESCRIPTION
    扫描 src/ 下的生产代码（排除 Smoke/、obj/、bin/、探针窗口），找出：
      - File.WriteAllText / WriteAllBytes / WriteAllLines（含 Async）
      - new FileStream(... FileMode.Create/CreateNew/Truncate)
      - File.Copy

    只允许两类：
      1. 目标路径是临时名（.part / .tmp / .ets-stage / TempSuffix / 含 temp/staging 的变量）；
      2. 文件在 REVIEWED 白名单里，且白名单注释写明为什么安全。

    退出码：0 = 没有可疑写入；1 = 有可疑写入（逐条列出）。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$srcRoot = Join-Path $repoRoot 'src'

# 已逐处评审、确认安全的文件（原因写在注释里；新增条目必须在审阅里说明）。
$reviewedFiles = @{
    # AtomicFile 自身：它就是"临时文件 + File.Move"的实现。
    'src\EbookToolSuite.Core\IO\AtomicFile.cs' = '原子写工具本身的实现'

    # AppPaths.IsWritable 的写权限探测：立刻删除，不承载任何用户数据。
    'src\EbookToolSuite.Infrastructure\Config\AppPaths.cs' = '只含写权限探测（写一个字节后立即删除）'

    # T7-1 启动耗时探针：参数是开发者指定的绝对路径，属于诊断产物。
    'src\EbookToolSuite.App\App.xaml.cs' = '只含 T7-1 启动探针的 JSON 报告'

    # 解包到"刚创建、还没发布"的工作区会话目录；目录整体就绪后才原子改名。
    'src\EbookToolSuite.Services\Editing\EpubWorkspaceFactory.cs' = '解包到尚未发布的新会话目录（目录级 staging）'
}

$rules = @(
    @{
        Name    = 'File.WriteAll*'
        Regex   = '(?<!Atomic)File\.Write(All(Text|Bytes|Lines)|AllTextAsync|AllBytesAsync)\s*\('
        Require = $null
        Allow   = '(?i)(part|tmp|temp|staging|AtomicFile|probe)'
    },
    @{
        Name    = 'FileStream(FileMode.Create)'
        Regex   = 'new FileStream\s*\('
        Require = 'FileMode\.(Create|CreateNew|Truncate)'
        Allow   = '(?i)(part|tmp|temp|staging|AtomicFile)'
    },
    @{
        Name    = 'File.Copy'
        Regex   = '(?<!Atomic)File\.Copy\s*\('
        Require = $null
        Allow   = '(?i)(part|tmp|temp|staging)'
    }
)

$excludedDirs = @('Smoke', 'obj', 'bin', 'Probes')
$excludedFilePatterns = @('*Probe*.cs', 'SmokeWebViewWindow.xaml.cs')

$files = Get-ChildItem -LiteralPath $srcRoot -Recurse -Filter '*.cs' -File |
    Where-Object {
        $relative = $_.FullName.Substring($srcRoot.Length + 1)
        $parts = $relative.Split([IO.Path]::DirectorySeparatorChar)

        $inExcludedDir = $false
        foreach ($part in $parts) {
            if ($excludedDirs -contains $part) { $inExcludedDir = $true; break }
        }

        if ($inExcludedDir) { return $false }

        foreach ($pattern in $excludedFilePatterns) {
            if ($_.Name -like $pattern) { return $false }
        }

        return $true
    }

$findings = @()

foreach ($file in $files) {
    $relative = $file.FullName.Substring($repoRoot.Length + 1)

    if ($reviewedFiles.ContainsKey($relative)) {
        continue
    }

    $lines = Get-Content -LiteralPath $file.FullName -Encoding UTF8

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        foreach ($rule in $rules) {
            if ($line -notmatch $rule.Regex) {
                continue
            }

            # 取当前行 + 上一行 + 下一行做上下文判断：临时名常在参数里跨行。
            $contextStart = [Math]::Max(0, $i - 1)
            $contextEnd = [Math]::Min($lines.Count - 1, $i + 1)
            $context = ($lines[$contextStart..$contextEnd] -join ' ')

            if ($null -ne $rule.Require -and $context -notmatch $rule.Require) {
                continue
            }

            if ($context -match $rule.Allow) {
                continue
            }

            $findings += [pscustomobject]@{
                File = $relative
                Line = $i + 1
                Rule = $rule.Name
                Code = $line.Trim()
            }

            break
        }
    }
}

if ($findings.Count -eq 0) {
    Write-Host '原子写审查：未发现直接写最终路径的位置。' -ForegroundColor Green
    exit 0
}

Write-Host "原子写审查：发现 $($findings.Count) 处可疑写入。" -ForegroundColor Red
Write-Host ''

foreach ($finding in $findings) {
    Write-Host ("  {0}:{1}  [{2}]" -f $finding.File, $finding.Line, $finding.Rule) -ForegroundColor Yellow
    Write-Host ("      {0}" -f $finding.Code) -ForegroundColor DarkGray
}

Write-Host ''
Write-Host '修复方式：改用 EbookToolSuite.Core.IO.AtomicFile（临时文件 + File.Move），' -ForegroundColor Yellow
Write-Host '或在 build\atomic-write-audit.ps1 的 REVIEWED 白名单里写明为什么安全。' -ForegroundColor Yellow

exit 1
