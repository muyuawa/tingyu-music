param([string]$Source=(Join-Path $PSScriptRoot '..\artifacts\portable'))
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
[xml]$properties=Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props')
$version=$properties.Project.PropertyGroup.Version
$files=[Collections.Generic.List[object]]::new()
function Include([string]$Path,[string]$Relative){$files.Add([pscustomobject]@{Path=$Path;Relative=$Relative.Replace('\','/')})}
foreach($file in Get-ChildItem -LiteralPath $repo -File){Include $file.FullName $file.Name}
foreach($folder in 'src','scripts','licenses','examples'){
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $repo $folder) -Recurse -File | Where-Object {$_.FullName -notmatch '\\(bin|obj)\\'}){Include $file.FullName $file.FullName.Substring($repo.Length+1)}
}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $repo 'native') -File){Include $file.FullName ('native/'+$file.Name)}
foreach($folder in 'LiquidGlassWinUI','Native'){
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $repo ('native\vendor\LiquidGlassWinUI\'+$folder)) -Recurse -File | Where-Object {$_.FullName -notmatch '\\(bin|obj|Output|Generated Files)\\'}){Include $file.FullName $file.FullName.Substring($repo.Length+1)}
}
foreach($name in 'LICENSE','MUYU_INTEGRATION.md','README.md'){Include (Join-Path $repo ('native\vendor\LiquidGlassWinUI\'+$name)) ('native/vendor/LiquidGlassWinUI/'+$name)}
foreach($name in 'ffmpeg-minimal-source.tar.gz','ffmpeg-source.json','flac-e94ff9f68b8e7dbd3e9f8b1ac18a8eca1914f181.tar.gz'){Include (Join-Path $repo ('native\vendor\'+$name)) ('native/vendor/'+$name)}
Include (Join-Path $Source 'licenses\sources\TagLibSharp-2.3.0-library-source.zip') 'native/vendor/TagLibSharp-2.3.0-library-source.zip'
foreach($file in Get-ChildItem -LiteralPath (Join-Path $repo 'native\vendor\ffmpeg-minimal\include') -Recurse -File){Include $file.FullName $file.FullName.Substring($repo.Length+1)}
foreach($name in 'avcodec-62.dll','avformat-62.dll','avutil-60.dll','swresample-6.dll','avcodec.lib','avformat.lib','avutil.lib','swresample.lib'){Include (Join-Path $repo ('native\vendor\ffmpeg-minimal\bin\'+$name)) ('native/vendor/ffmpeg-minimal/bin/'+$name)}
$destination=Join-Path $repo ('artifacts\MuyuMusic-'+$version+'-source.zip')
if(Test-Path -LiteralPath $destination){$destination=Join-Path $repo ('artifacts\MuyuMusic-'+$version+'-source-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.zip')}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::Open($destination,[IO.Compression.ZipArchiveMode]::Create)
try{foreach($file in $files | Sort-Object Relative){if($file.Relative -match '(^|/)(obj|artifacts|data|ffbuild)/|config\.log$'){throw 'Unexpected source package entry'};[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.Path,('MuyuMusic-source/'+$file.Relative),[IO.Compression.CompressionLevel]::Optimal) | Out-Null}}finally{$zip.Dispose()}
Get-FileHash -LiteralPath $destination -Algorithm SHA256
