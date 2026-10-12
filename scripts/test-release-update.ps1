$ErrorActionPreference='Stop'
$fixture=Join-Path (Split-Path $PSScriptRoot) ('artifacts\release-1.0\update-test-'+[guid]::NewGuid().ToString('N'))
$source=Join-Path $fixture 'source';$target=Join-Path $fixture 'target'
New-Item -ItemType Directory -Path $source,(Join-Path $target 'data') -Force | Out-Null
foreach($name in 'Muyu.App.exe','Muyu.App.dll','README.md'){
    [IO.File]::WriteAllText((Join-Path $source $name),'new-'+$name)
    [IO.File]::WriteAllText((Join-Path $target $name),'old-'+$name)
}
[IO.File]::WriteAllText((Join-Path $target 'data\personal.txt'),'personal-data-preserved')
[IO.File]::WriteAllText((Join-Path $target 'Muyu.App.pdb'),'old-debug-symbol')
[IO.File]::WriteAllText((Join-Path $target 'UI_RENDERING.md'),'original-doc')
$oldFiles=[ordered]@{}
foreach($file in Get-ChildItem -LiteralPath $target -File){$oldFiles[$file.Name]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
@{version='0.14.3';files=$oldFiles} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'PACKAGE_MANIFEST.json') -Encoding UTF8
[IO.File]::WriteAllText((Join-Path $target 'UI_RENDERING.md'),'user-edited-doc')
$newFiles=[ordered]@{}
foreach($file in Get-ChildItem -LiteralPath $source -File){$newFiles[$file.Name]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
@{version='1.0.0';files=$newFiles} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $source 'PACKAGE_MANIFEST.json') -Encoding UTF8
$result=& (Join-Path $PSScriptRoot 'Update-InstalledMuyuMusic.ps1') -Source $source -Target $target | ConvertFrom-Json
if([IO.File]::ReadAllText((Join-Path $target 'Muyu.App.exe')) -ne 'new-Muyu.App.exe'){throw 'Program was not updated'}
if([IO.File]::ReadAllText((Join-Path $target 'data\personal.txt')) -ne 'personal-data-preserved'){throw 'Personal data changed'}
if(Test-Path -LiteralPath (Join-Path $target 'Muyu.App.pdb')){throw 'Owned obsolete debug file remains'}
if([IO.File]::ReadAllText((Join-Path $target 'UI_RENDERING.md')) -ne 'user-edited-doc'){throw 'Modified user document changed'}
if(-not (Test-Path -LiteralPath (Join-Path $result.backup 'Muyu.App.pdb'))){throw 'Removed debug file was not backed up'}
$unsafe=@{version='0.14.3';files=@{'..\escape.pdb'='not-a-hash'}}
$unsafe | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'PACKAGE_MANIFEST.json') -Encoding UTF8
$rejected=$false
try{& (Join-Path $PSScriptRoot 'Update-InstalledMuyuMusic.ps1') -Source $source -Target $target | Out-Null}catch{if($_.Exception.Message -match '旧调试文件路径无效'){$rejected=$true}else{throw}}
if(-not $rejected){throw 'Unsafe old manifest path was not rejected'}
[pscustomobject]@{pass=$true;program_updated=$true;personal_data_unchanged=$true;obsolete_debug_removed_and_backed_up=$true;user_modified_document_preserved=$true;unsafe_old_path_rejected=$true;fixture=$fixture} | ConvertTo-Json
