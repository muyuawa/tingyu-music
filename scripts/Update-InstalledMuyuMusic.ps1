param(
    [string]$Source=(Join-Path $PSScriptRoot '..\artifacts\release-1.0\portable'),
    [string]$Target='D:\MuyuMusic'
)
$ErrorActionPreference='Stop'
$sourceRoot=(Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
$targetRoot=(Resolve-Path -LiteralPath $Target).Path.TrimEnd('\')
if($targetRoot -eq [IO.Path]::GetPathRoot($targetRoot).TrimEnd('\') -or $targetRoot -eq $sourceRoot){throw '更新目录无效'}
if(-not (Test-Path -LiteralPath (Join-Path $targetRoot 'Muyu.App.exe'))){throw '目标目录中没有 Muyu Music'}
foreach($root in $sourceRoot,$targetRoot){if((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '更新目录不能是目录链接'}}
$running=@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($targetRoot+'\',[StringComparison]::OrdinalIgnoreCase)})
if($running.Count){throw '请先退出 Muyu Music，包括托盘中的播放器；更新不会强制结束进程。'}
$manifest=Get-Content -LiteralPath (Join-Path $sourceRoot 'PACKAGE_MANIFEST.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($manifest.version -ne '1.0.0'){throw '更新包版本不符'}
$entries=@($manifest.files.PSObject.Properties)
$obsolete=[Collections.Generic.List[object]]::new()
$oldManifestPath=Join-Path $targetRoot 'PACKAGE_MANIFEST.json'
if(Test-Path -LiteralPath $oldManifestPath){
    $oldManifest=Get-Content -LiteralPath $oldManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach($entry in $oldManifest.files.PSObject.Properties){
        if($manifest.files.PSObject.Properties.Name -contains $entry.Name){continue}
        $relative=$entry.Name.Replace('/','\')
        if($relative -notin @('BUILD_INFO.json','VERIFICATION.json','IMPLEMENTATION_STATUS.md','UI_RENDERING.md','UI_REQUIREMENTS.md') -and [IO.Path]::GetExtension($relative) -ne '.pdb'){continue}
        $absolute=[IO.Path]::GetFullPath((Join-Path $targetRoot $relative))
        if(-not $absolute.StartsWith($targetRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $relative.StartsWith('data\',[StringComparison]::OrdinalIgnoreCase)){throw '旧调试文件路径无效'}
        $ancestor=$absolute
        while($ancestor -and $ancestor -ne $targetRoot){
            if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '旧调试文件路径包含链接'}
            $ancestor=Split-Path $ancestor -Parent
        }
        if((Test-Path -LiteralPath $absolute -PathType Leaf) -and (Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash -eq $entry.Value){$obsolete.Add($entry)}
    }
}
foreach($entry in $entries){
    $relative=$entry.Name.Replace('/','\')
    if($relative -eq 'data' -or $relative.StartsWith('data\',[StringComparison]::OrdinalIgnoreCase)){throw '更新包包含个人数据'}
    foreach($root in $sourceRoot,$targetRoot){
        $absolute=[IO.Path]::GetFullPath((Join-Path $root $relative))
        if(-not $absolute.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw '更新包路径超出目标目录'}
        $ancestor=Split-Path $absolute -Parent
        while($ancestor -and $ancestor -ne $root){
            if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '更新路径包含目录链接'}
            $ancestor=Split-Path $ancestor -Parent
        }
        if((Test-Path -LiteralPath $absolute) -and ((Get-Item -LiteralPath $absolute).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '更新文件不能是链接'}
    }
    if((Get-FileHash -LiteralPath (Join-Path $sourceRoot $relative) -Algorithm SHA256).Hash -ne $entry.Value){throw "更新文件摘要不符：$relative"}
}
$parent=Split-Path $targetRoot -Parent
$backup=[IO.Path]::GetFullPath((Join-Path $parent ('MuyuMusic-backup-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))))
if(-not $backup.StartsWith($parent.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw '备份路径无效'}
New-Item -ItemType Directory -Path $backup | Out-Null
$saved=[Collections.Generic.List[string]]::new()
foreach($entry in (@($entries)+@($obsolete.ToArray()))){
    $relative=$entry.Name.Replace('/','\');$old=Join-Path $targetRoot $relative
    if(Test-Path -LiteralPath $old){$copy=Join-Path $backup $relative;New-Item -ItemType Directory -Path (Split-Path $copy) -Force | Out-Null;Copy-Item -LiteralPath $old -Destination $copy;$saved.Add($relative)}
}
try{
    foreach($entry in $entries){
        $relative=$entry.Name.Replace('/','\');$destination=Join-Path $targetRoot $relative
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $sourceRoot $relative) -Destination $destination -Force
    }
    foreach($entry in $entries){if((Get-FileHash -LiteralPath (Join-Path $targetRoot $entry.Name) -Algorithm SHA256).Hash -ne $entry.Value){throw "更新后摘要不符：$($entry.Name)"}}
    foreach($entry in $obsolete){Remove-Item -LiteralPath (Join-Path $targetRoot $entry.Name) -Force}
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'PACKAGE_MANIFEST.json') -Destination (Join-Path $targetRoot 'PACKAGE_MANIFEST.json') -Force
}catch{
    foreach($relative in $saved){Copy-Item -LiteralPath (Join-Path $backup $relative) -Destination (Join-Path $targetRoot $relative) -Force}
    throw
}
[pscustomobject]@{version='1.0.0';target=$targetRoot;backup=$backup;data_preserved=$true;processes_stopped=$false;files_verified=$entries.Count;obsolete_debug_files_removed=$obsolete.Count} | ConvertTo-Json
