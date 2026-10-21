<#
.SYNOPSIS
    T5-7 阶段 5 验收总控（9 条验收标准原样，只做验收与取证，不修改计划文档）。
.DESCRIPTION
    1) dotnet test 引擎层全量（含判据2/3/4/5/6：改结构后重打包 EpubCheck 零 error）
    2) dotnet test 服务层全量（保存闭环 / 查找替换 / 快照）
    3) 定向跑 判据3（删章）/判据5（新增）/判据4（移动）/源码改过正文/判据2（改名）
    4) build\smoke-edit.ps1  编辑器端到端
    5) build\smoke-reader.ps1 阅读器端到端（含 T5-3 查找替换）
    6) 对编辑器冒烟真实保存出的 EPUB 做 Calibre 可读性 + EpubCheck 门禁
    输出 build\t57-acceptance-report.md 与 .json。
    退出码：0=9 步全绿；2=有步骤失败；1=跑不起来。
#>
[CmdletBinding()]
param(
    [string]$EditFile,
    [string]$ReaderFile,
    [string]$DataDir,
    [ValidateSet('Debug','Release')][string]$Configuration='Debug',
    [switch]$SkipTests,
    [switch]$SkipSmoke,
    [switch]$SkipCalibre,
    [int]$TimeoutSeconds=900
)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
if([string]::IsNullOrWhiteSpace($EditFile)){ $EditFile=Join-Path $repoRoot 'TestCorpus\t56-valid-5ch.epub' }
if([string]::IsNullOrWhiteSpace($ReaderFile)){ $ReaderFile=Join-Path $repoRoot 'TestCorpus\sample-epub-1.epub' }
$EditFile=[IO.Path]::GetFullPath($EditFile); $ReaderFile=[IO.Path]::GetFullPath($ReaderFile)
if([string]::IsNullOrWhiteSpace($DataDir)){ $DataDir=Join-Path $repoRoot 'Data-Test\t57-acceptance' }
$DataDir=[IO.Path]::GetFullPath($DataDir)
if(Test-Path -LiteralPath $DataDir){ Remove-Item -LiteralPath $DataDir -Recurse -Force }
$editData=Join-Path $DataDir 'edit'; $readerData=Join-Path $DataDir 'reader'; $probeDir=Join-Path $DataDir 'probe'; $logsDir=Join-Path $DataDir 'logs'
foreach($d in @($DataDir,$editData,$readerData,$probeDir,$logsDir)){ New-Item -ItemType Directory -Force -Path $d | Out-Null }
Write-Host '==== T5-7 阶段 5 验收总控 ===='
Write-Host "编辑用书：$EditFile"; Write-Host "阅读用书：$ReaderFile"; Write-Host "数据目录：$DataDir"
foreach($f in @($EditFile,$ReaderFile)){ if(-not(Test-Path -LiteralPath $f)){ Write-Host "找不到输入书：$f" -ForegroundColor Red; exit 1 } }
function Invoke-Native([string]$FilePath,[string[]]$Arguments){ $p=$ErrorActionPreference; $ErrorActionPreference='Continue'; try{ $o=& $FilePath @Arguments 2>&1; return [pscustomobject]@{ExitCode=$LASTEXITCODE;Output=($o|Out-String)} } finally{ $ErrorActionPreference=$p } }
function Get-TestCount([string]$Output){ $pa=-1;$fa=-1; foreach($x in ($Output -split "`r?`n")){ if($x -match '通过:\s*(\d+)' -or $x -match 'Passed:\s*(\d+)'){$pa=[int]$Matches[1]}; if($x -match '失败:\s*(\d+)' -or $x -match 'Failed:\s*(\d+)'){$fa=[int]$Matches[1]} }; return [pscustomobject]@{Passed=$pa;Failed=$fa} }
function Invoke-TestProject([string]$Project,[string]$LogName,[string]$Filter){ $a=@('test',$Project,'-c',$Configuration,'--nologo'); if(-not[string]::IsNullOrWhiteSpace($Filter)){$a+=@('--filter',$Filter)}; $r=Invoke-Native 'dotnet' $a; Set-Content -LiteralPath (Join-Path $logsDir $LogName) -Value $r.Output -Encoding UTF8; $c=Get-TestCount $r.Output; return [pscustomobject]@{ExitCode=$r.ExitCode;Passed=$c.Passed;Failed=$c.Failed;Log=(Join-Path $logsDir $LogName)} }
$enginesProj=Join-Path $repoRoot 'tests\EbookToolSuite.Engines.Tests\EbookToolSuite.Engines.Tests.csproj'
$servicesProj=Join-Path $repoRoot 'tests\EbookToolSuite.Services.Tests\EbookToolSuite.Services.Tests.csproj'
$fullEngines=$null;$fullServices=$null;$focused=@{}
if(-not $SkipTests){
  Write-Host '--- 引擎层全量 ---'; $fullEngines=Invoke-TestProject $enginesProj 'engines-tests.log' $null; Write-Host ("    exit {0}，通过 {1}，失败 {2}" -f $fullEngines.ExitCode,$fullEngines.Passed,$fullEngines.Failed)
  Write-Host '--- 服务层全量 ---'; $fullServices=Invoke-TestProject $servicesProj 'services-tests.log' $null; Write-Host ("    exit {0}，通过 {1}，失败 {2}" -f $fullServices.ExitCode,$fullServices.Passed,$fullServices.Failed)
  foreach($pair in @(@('判据3','delete'),@('判据5','add'),@('判据4','move'),@('源码改过正文','edit-body'),@('判据2','rename'))){ $k=$pair[1]; $focused[$k]=Invoke-TestProject $enginesProj ("engines-$k.log") ('FullyQualifiedName~'+$pair[0]); Write-Host ("    定向 {0}：exit {1}，通过 {2}，失败 {3}" -f $pair[0],$focused[$k].ExitCode,$focused[$k].Passed,$focused[$k].Failed) }
}
$editReportPath=Join-Path $editData 'Temp\edit-smoke.json'; $readerReportPath=Join-Path $readerData 'Temp\reader-smoke.json'; $editExit=-1;$readerExit=-1
if(-not $SkipSmoke){
  Write-Host '--- 编辑器冒烟 ---'; & (Join-Path $PSScriptRoot 'smoke-edit.ps1') -File $EditFile -DataDir $editData -Configuration $Configuration -TimeoutSeconds $TimeoutSeconds -KeepDataDir; $editExit=$LASTEXITCODE; Write-Host "    exit=$editExit"
  Write-Host '--- 阅读器冒烟 ---'; & (Join-Path $PSScriptRoot 'smoke-reader.ps1') -File $ReaderFile -DataDir $readerData -Configuration $Configuration -KeepDataDir; $readerExit=$LASTEXITCODE; Write-Host "    exit=$readerExit"
}
function Read-Json([string]$Path){ if(Test-Path -LiteralPath $Path){ return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json) }; return $null }
$editReport=Read-Json $editReportPath; $readerReport=Read-Json $readerReportPath
$editChecks=if($null -ne $editReport){$editReport.Checks}else{$null}; $readerChecks=if($null -ne $readerReport){$readerReport.Checks}else{$null}
function Get-Check([object]$Checks,[string]$Name){ if($null -eq $Checks){return $null}; return ($Checks|Where-Object{$_.Name -eq $Name}|Select-Object -First 1) }
function Test-CheckNames([object]$Checks,[string[]]$Names){ $mi=@();$fa=@(); foreach($n in $Names){ $c=Get-Check $Checks $n; if($null -eq $c){$mi+=$n} elseif(-not $c.Ok){$fa+=$n} }; return [pscustomobject]@{Ok=($mi.Count -eq 0 -and $fa.Count -eq 0);Detail=("缺 {0} / 失败 {1}" -f $mi.Count,$fa.Count)} }
function Get-FocusedOk([string]$Key){ return ($focused.ContainsKey($Key) -and $focused[$Key].ExitCode -eq 0 -and $focused[$Key].Passed -gt 0) }
function Get-FocusedExit([string]$Key){ if($focused.ContainsKey($Key)){return $focused[$Key].ExitCode}; return 'N/A' }
$savedBook=$null; $lib=Join-Path $editData 'Library'; if(Test-Path -LiteralPath $lib){ $savedBook=Get-ChildItem -LiteralPath $lib -Recurse -Filter '*.epub' -File | Select-Object -First 1 }
$calMeta=$null;$calConv=$null;$calTxt=$null;$calOk=$false;$calDetail='未执行'
$ecOk=$false;$ecFatal=-1;$ecError=-1;$ecWarn=-1;$ecJson=$null
if(-not $SkipCalibre -and $null -ne $savedBook){
  $metaExe=Join-Path $repoRoot 'Tools\Calibre Portable\Calibre\ebook-meta.exe'; $convExe=Join-Path $repoRoot 'Tools\Calibre Portable\Calibre\ebook-convert.exe'
  if((Test-Path -LiteralPath $metaExe) -and (Test-Path -LiteralPath $convExe)){ $calMeta=Invoke-Native $metaExe @($savedBook.FullName); $calTxt=Join-Path $probeDir 'calibre-out.txt'; $calConv=Invoke-Native $convExe @($savedBook.FullName,$calTxt); $txtOk=((Test-Path -LiteralPath $calTxt) -and ((Get-Item -LiteralPath $calTxt).Length -gt 0)); $calOk=($calMeta.ExitCode -eq 0 -and $calConv.ExitCode -eq 0 -and $txtOk); $calDetail=("ebook-meta exit={0}；ebook-convert exit={1}；TXT 非空={2}" -f $calMeta.ExitCode,$calConv.ExitCode,$txtOk) } else { $calDetail='Calibre 工具缺失' }
  $jar=Join-Path $repoRoot 'Tools\EpubCheck\epubcheck.jar'
  if(Test-Path -LiteralPath $jar){ $ecJson=Join-Path $probeDir 'epubcheck.json'; $ec=Invoke-Native 'java' @('-jar',$jar,$savedBook.FullName,'--json',$ecJson); if(Test-Path -LiteralPath $ecJson){ $j=Get-Content -LiteralPath $ecJson -Raw -Encoding UTF8 | ConvertFrom-Json; $ecFatal=[int]$j.checker.nFatal; $ecError=[int]$j.checker.nError; $ecWarn=[int]$j.checker.nWarning; $ecOk=($ecFatal -eq 0 -and $ecError -eq 0) } }
}
$steps=New-Object System.Collections.Generic.List[object]
function Add-Step([int]$No,[string]$Title,[bool]$Ok,[string]$Evidence){ $steps.Add([pscustomobject]@{No=$No;Title=$Title;Ok=$Ok;Evidence=$Evidence}) }
$s1=Test-CheckNames $editChecks @('A-three-entries-exist','B-entry-opens-editor-window','B-same-book-single-window','C-workspace-loaded','C-form-filled-from-opf')
Add-Step 1 '打开一本 EPUB。' ($s1.Ok -and $editExit -eq 0) ("编辑器冒烟入口/开窗/单实例/工作区/表单（{0}）；edit exit={1}" -f $s1.Detail,$editExit)
$s2=Test-CheckNames $editChecks @('Q4-delete-syncs-spine-manifest-toc')
Add-Step 2 '删掉一章 -> 保存 -> EpubCheck 零 error -> Calibre 阅读器能打开。' ($s2.Ok -and (Get-FocusedOk 'delete') -and $ecOk -and $calOk) ("删章同步 spine/manifest/TOC（{0}）；引擎判据3 exit={1}；产物 EpubCheck fatal={2}/error={3}；Calibre {4}" -f $s2.Detail,(Get-FocusedExit 'delete'),$ecFatal,$ecError,$calDetail)
$s3a=Test-CheckNames $editChecks @('Q5-add-empty-chapter','S2-source-save-writes-workspace'); $s3b=Test-CheckNames $readerChecks @('G-chapter-rendered','Y4b-edit-save-reloads-chapter-and-keeps-position','Y4c-edit-reopen-shows-saved-content'); $txtNonEmpty=((Test-Path -LiteralPath $calTxt) -and ((Get-Item -LiteralPath $calTxt).Length -gt 0))
Add-Step 3 '新增一章，写点内容 -> 保存 -> 在阅读器里能看到。' ($s3a.Ok -and (Get-FocusedOk 'add') -and $s3b.Ok -and $txtNonEmpty) ("新增空章+源码写盘（{0}）；引擎判据5 exit={1}；阅读器渲染/保存后可见（{2}）；Calibre 转换非空={3}" -f $s3a.Detail,(Get-FocusedExit 'add'),$s3b.Detail,$txtNonEmpty)
$s4=Test-CheckNames $editChecks @('Q3-move-changes-spine-order')
Add-Step 4 '移动章节顺序 -> 保存 -> 顺序正确。' ($s4.Ok -and (Get-FocusedOk 'move')) ("移动章节同步 spine 顺序（{0}）；引擎判据4 exit={1}" -f $s4.Detail,(Get-FocusedExit 'move'))
$s5a=Test-CheckNames $editChecks @('S2-source-save-writes-workspace'); $s5b=Test-CheckNames $readerChecks @('Y4b-edit-save-reloads-chapter-and-keeps-position')
Add-Step 5 '改一章正文 -> 保存。' ($s5a.Ok -and (Get-FocusedOk 'edit-body') -and $s5b.Ok) ("源码保存写盘（{0}）；引擎源码改正文 exit={1}；阅读器保存后重载（{2}）" -f $s5a.Detail,(Get-FocusedExit 'edit-body'),$s5b.Detail)
$s6a=Test-CheckNames $readerChecks @('T53-book-scope-preview-cancel','T53-replace-all-writes-and-reloads','T53-illegal-replacement-writes-nothing','T53-cancel-writes-nothing'); $s6b=Test-CheckNames $editChecks @('F-package-verifier-passed','F-title-written-to-epub')
Add-Step 6 '全书查找替换"的"->"之"，先看预览，再执行 -> 保存 -> 校验通过。' ($s6a.Ok -and $s6b.Ok -and $ecOk) ("T53 预览/取消/非法不写盘/执行写盘（{0}）；主窗口保存+打包自检（{1}）；产物 EpubCheck fatal={2}/error={3}" -f $s6a.Detail,$s6b.Detail,$ecFatal,$ecError)
$s7=Test-CheckNames $editChecks @('S3-unclosed-br-blocked','S4-bare-amp-blocked','V1-visual-save-asks-for-fix','V2-visual-save-applies-fixes-and-writes','M-undeclared-blocked-and-fixable')
Add-Step 7 '故意把一章改坏（删掉 </p>）-> 保存被拦，定位准确。' $s7.Ok ("源码未闭合标签/裸 & 拦截、可视模式保存前弹修正清单、未声明资源拦截（{0}）" -f $s7.Detail)
$s8a=Test-CheckNames $editChecks @('Q7-revert-goes-back-to-session-start'); $s8b=Test-CheckNames $readerChecks @('T54-initial-snapshot','T54-undo-last-replace')
Add-Step 8 '还原到"打开时"的快照 -> 一切复原。' ($s8a.Ok -and $s8b.Ok) ("章节树回到会话起点（{0}）；阅读器初始快照+撤销替换（{1}）" -f $s8a.Detail,$s8b.Detail)
$s9=Test-CheckNames $editChecks @('N-save-created-restore-point','O-restore-recovers-content','O-reverse-restore-point-created','R-restore-undoes-forced-save')
Add-Step 9 '从备份还原整本书 -> 复原。' $s9.Ok ("保存前还原点/还原恢复内容/反向还原点/强制保存可还原（{0}）" -f $s9.Detail)
$testsOk=$SkipTests -or (($null -ne $fullEngines -and $fullEngines.ExitCode -eq 0 -and $fullEngines.Failed -eq 0) -and ($null -ne $fullServices -and $fullServices.ExitCode -eq 0 -and $fullServices.Failed -eq 0))
$smokeOk=$SkipSmoke -or ($editExit -eq 0 -and $readerExit -eq 0)
$gatesOk=$SkipCalibre -or ($calOk -and $ecOk)
$allOk=(($steps|Where-Object{-not $_.Ok}).Count -eq 0) -and $testsOk -and $smokeOk -and $gatesOk
$md=New-Object System.Collections.Generic.List[string]
$md.Add('# T5-7 阶段 5 验收报告'); $md.Add('')
$md.Add(("- 生成时间：{0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')))
$md.Add(("- 编辑用书：{0}" -f $EditFile)); $md.Add(("- 阅读用书：{0}" -f $ReaderFile)); $md.Add(("- 数据目录：{0}" -f $DataDir))
$md.Add(("- 保存产物：{0}" -f $(if($null -ne $savedBook){$savedBook.FullName}else{'(无)'})))
$md.Add(("- 结论：{0}" -f $(if($allOk){'9 步全部通过'}else{'有步骤未通过'}))); $md.Add('')
$md.Add('## 9 条验收标准与结果（标准原样不改）'); $md.Add('')
$md.Add('| 步骤 | 验收标准 | 结果 | 证据 |'); $md.Add('| --- | --- | --- | --- |')
foreach($s in $steps){ $md.Add(("| {0} | {1} | {2} | {3} |" -f $s.No,$s.Title,$(if($s.Ok){'PASS'}else{'FAIL'}),$s.Evidence)) }
$md.Add(''); $md.Add('## 全量门禁'); $md.Add('')
if($null -ne $fullEngines){ $md.Add(("- 引擎层：exit {0}，通过 {1}，失败 {2}" -f $fullEngines.ExitCode,$fullEngines.Passed,$fullEngines.Failed)) }
if($null -ne $fullServices){ $md.Add(("- 服务层：exit {0}，通过 {1}，失败 {2}" -f $fullServices.ExitCode,$fullServices.Passed,$fullServices.Failed)) }
if($null -ne $editReport){ $md.Add(("- 编辑器冒烟：{0} 项，exit {1}，{2} ms" -f $editReport.Checks.Count,$editExit,$editReport.DurationMs)) }
if($null -ne $readerReport){ $md.Add(("- 阅读器冒烟：{0} 项，exit {1}，{2} ms" -f $readerReport.Checks.Count,$readerExit,$readerReport.DurationMs)) }
$md.Add(("- Calibre：{0}" -f $calDetail)); $md.Add(("- EpubCheck：fatal={0} / error={1} / warning={2}" -f $ecFatal,$ecError,$ecWarn)); $md.Add('')
$md.Add('## 人工复核项'); $md.Add('- Calibre 图形阅读器真开一次。'); $md.Add('- 步骤 7 的行号定位精度。'); $md.Add('- 步骤 8/9 的备份中心可视化还原流程。')
$reportMd=Join-Path $repoRoot 'build\t57-acceptance-report.md'; Set-Content -LiteralPath $reportMd -Value ($md -join "`r`n") -Encoding UTF8
$jsonOut=Join-Path $repoRoot 'build\t57-acceptance-report.json'
[pscustomobject]@{GeneratedAt=(Get-Date).ToString('o');AllOk=$allOk;Steps=$steps;EditExit=$editExit;ReaderExit=$readerExit;Engines=$fullEngines;Services=$fullServices;Focused=$focused;CalibreOk=$calOk;CalibreDetail=$calDetail;EpubCheckOk=$ecOk;EpubCheckFatal=$ecFatal;EpubCheckError=$ecError;EpubCheckWarning=$ecWarn;SavedBook=$(if($null -ne $savedBook){$savedBook.FullName}else{$null});Report=$reportMd} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonOut -Encoding UTF8
Write-Host ''; Write-Host '==== T5-7 验收结果 ===='
foreach($s in $steps){ $c=if($s.Ok){'Green'}else{'Red'}; $t=if($s.Ok){'PASS'}else{'FAIL'}; Write-Host ("[{0}] 步{1} {2}" -f $t,$s.No,$s.Title) -ForegroundColor $c; Write-Host ("        证据：{0}" -f $s.Evidence) }
Write-Host ("报告：{0}" -f $reportMd); Write-Host ("数据：{0}" -f $DataDir)
if(-not $allOk){ exit 2 }; exit 0
