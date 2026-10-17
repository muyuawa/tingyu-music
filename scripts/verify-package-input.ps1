param([string]$Source=(Join-Path $PSScriptRoot '..\artifacts\portable'))
$ErrorActionPreference='Stop'
$sourceRoot=(Resolve-Path -LiteralPath $Source).Path
[xml]$properties=Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot) 'Directory.Build.props')
foreach($resource in 'Muyu.App.pri','App.xbf','MainWindow.xbf','MuyuAudio.dll','audio-host\MuyuAudio.dll','portable.marker'){
    if(-not (Test-Path -LiteralPath (Join-Path $sourceRoot $resource))){throw "Required package file is missing: $resource"}
}
$report=Get-Content -LiteralPath (Join-Path $sourceRoot 'BUILD_INFO.json') -Raw | ConvertFrom-Json
if($report.build -ne $properties.Project.PropertyGroup.Version){throw 'Run build.ps1 for the current version before packaging.'}
foreach($assembly in 'Muyu.App.dll','Muyu.Core.dll','audio-host\Muyu.AudioHost.dll','audio-host\Muyu.Core.dll'){
    $version=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $sourceRoot $assembly)).Version
    if($version.ToString() -ne $properties.Project.PropertyGroup.AssemblyVersion){throw "Wrong packaged version: $assembly"}
}
if((Get-FileHash -LiteralPath (Join-Path $sourceRoot 'MuyuAudio.dll')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $sourceRoot 'audio-host\MuyuAudio.dll')).Hash){throw 'Native libraries differ between app and host'}
$ffmpeg=Get-Content -LiteralPath (Join-Path $sourceRoot 'licenses\sources\ffmpeg-source.json') -Raw | ConvertFrom-Json
foreach($library in $ffmpeg.libraries){foreach($folder in $sourceRoot,(Join-Path $sourceRoot 'audio-host')){if((Get-FileHash -LiteralPath (Join-Path $folder $library.name) -Algorithm SHA256).Hash -ne $library.sha256){throw 'FFmpeg binary does not match exact source manifest'}}}
$report
