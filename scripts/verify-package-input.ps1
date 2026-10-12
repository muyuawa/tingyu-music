param([string]$Source=(Join-Path $PSScriptRoot '..\artifacts\portable'),[switch]$BuildOnly)
$ErrorActionPreference='Stop'
$sourceRoot=(Resolve-Path -LiteralPath $Source).Path
[xml]$properties=Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot) 'Directory.Build.props')
foreach($resource in 'Muyu.App.pri','App.xbf','MainWindow.xbf'){if(-not (Test-Path -LiteralPath (Join-Path $sourceRoot $resource))){throw "Required application resource is missing: $resource"}}
if($BuildOnly){
    $report=Get-Content -LiteralPath (Join-Path $sourceRoot 'BUILD_INFO.json') -Raw | ConvertFrom-Json
    if($report.build -ne $properties.Project.PropertyGroup.Version -or $report.status -ne 'build_only'){throw 'Build-only package input does not match the current version.'}
    $report
    return
}
$report=Get-Content -LiteralPath (Join-Path $sourceRoot 'VERIFICATION.json') -Raw | ConvertFrom-Json
if($report.build -ne $properties.Project.PropertyGroup.Version -or -not $report.binaries){throw 'Run build.ps1 and test.ps1 for this version before packaging.'}
foreach($binary in $report.binaries.PSObject.Properties){$path=[IO.Path]::GetFullPath((Join-Path $sourceRoot $binary.Name));if(-not $path.StartsWith($sourceRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid verification path'};if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $binary.Value){throw "Binary changed after verification: $($binary.Name)"}}
$ffmpeg=Get-Content -LiteralPath (Join-Path $sourceRoot 'licenses\sources\ffmpeg-source.json') -Raw | ConvertFrom-Json
foreach($library in $ffmpeg.libraries){foreach($folder in $sourceRoot,(Join-Path $sourceRoot 'audio-host')){if((Get-FileHash -LiteralPath (Join-Path $folder $library.name) -Algorithm SHA256).Hash -ne $library.sha256){throw 'FFmpeg binary does not match exact source manifest'}}}
$report
