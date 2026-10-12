param([string]$Source=(Join-Path $PSScriptRoot 'artifacts\portable'),[switch]$BuildOnly)
$ErrorActionPreference='Stop'
$repo=$PSScriptRoot
$sourceRoot=(Resolve-Path -LiteralPath $Source).Path
$report=& (Join-Path $repo 'scripts\verify-package-input.ps1') -Source $sourceRoot -BuildOnly:$BuildOnly
. (Join-Path $repo 'scripts\release-files.ps1')
$files=@(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object {Test-ReleaseFile $_ $sourceRoot} | Sort-Object FullName)
$hashes=[ordered]@{}
foreach($file in $files){$hashes[$file.FullName.Substring($sourceRoot.Length+1).Replace('\','/')]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
[pscustomobject]@{version=$report.build;build_only=[bool]$BuildOnly;purpose='Portable payload; installer omits portable.marker. Personal data is excluded.';files=$hashes} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $sourceRoot 'PACKAGE_MANIFEST.json') -Encoding utf8
$files+=Get-Item -LiteralPath (Join-Path $sourceRoot 'PACKAGE_MANIFEST.json')
$destination=Join-Path $repo ('artifacts\Tingyu-'+$report.build+'-win-x64.zip')
if(Test-Path -LiteralPath $destination){$destination=Join-Path $repo ('artifacts\Tingyu-'+$report.build+'-win-x64-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.zip')}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::Open($destination,[IO.Compression.ZipArchiveMode]::Create)
try{foreach($file in $files){$entry='MuyuMusic/'+$file.FullName.Substring($sourceRoot.Length+1).Replace('\','/');[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$entry,[IO.Compression.CompressionLevel]::Optimal) | Out-Null}}finally{$zip.Dispose()}
Get-FileHash -LiteralPath $destination -Algorithm SHA256
