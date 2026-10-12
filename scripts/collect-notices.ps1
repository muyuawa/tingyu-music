param([string]$Destination = (Join-Path $PSScriptRoot '..\artifacts\portable'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$output = Join-Path $Destination 'licenses'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$packages=@{}
foreach($project in 'Muyu.App','Muyu.AudioHost','Muyu.Core'){
    $assets=Get-Content -LiteralPath (Join-Path $repo "src\$project\obj\project.assets.json") -Raw | ConvertFrom-Json
    $packageRoot=$assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
    foreach($entry in $assets.libraries.PSObject.Properties){if($entry.Value.type -eq 'package'){$packages[$entry.Name]=Join-Path $packageRoot $entry.Name.ToLowerInvariant()}}
}
$inventory=foreach($name in $packages.Keys | Sort-Object){
    $packageFolder=$packages[$name];$spec=Get-ChildItem -LiteralPath $packageFolder -Filter '*.nuspec' | Select-Object -First 1
    [xml]$metadata=Get-Content -LiteralPath $spec.FullName
    $target=Join-Path $output ($name -replace '/','-');New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath $spec.FullName -Destination $target
    foreach($notice in Get-ChildItem -LiteralPath $packageFolder -File | Where-Object {$_.Name -match 'license|notice|copying'}){Copy-Item -LiteralPath $notice.FullName -Destination $target}
    if($metadata.package.metadata.license.type -eq 'file'){$licensePath=Join-Path $packageFolder $metadata.package.metadata.license.InnerText;if(Test-Path -LiteralPath $licensePath){Copy-Item -LiteralPath $licensePath -Destination $target}}
    [pscustomobject]@{Package=$name;Type=$metadata.package.metadata.license.type;License=$metadata.package.metadata.license.InnerText;LicenseUrl=$metadata.package.metadata.licenseUrl}
}
$inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'package-inventory.json') -Encoding utf8
foreach($file in Get-ChildItem -LiteralPath (Join-Path $repo 'licenses') -File){Copy-Item -LiteralPath $file.FullName -Destination $output}
Copy-Item -LiteralPath (Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination $Destination
$sources=Join-Path $output 'sources';New-Item -ItemType Directory -Path $sources -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'native\vendor\ffmpeg-minimal-source.tar.gz') -Destination $sources
Copy-Item -LiteralPath (Join-Path $repo 'native\vendor\flac-e94ff9f68b8e7dbd3e9f8b1ac18a8eca1914f181.tar.gz') -Destination $sources
Copy-Item -LiteralPath (Join-Path $repo 'native\vendor\ffmpeg-source.json'),(Join-Path $repo 'scripts\build-ffmpeg.ps1') -Destination $sources
$tagRoot=Join-Path $repo 'native\vendor\taglib-sharp-TaglibSharp-2.3.0.0'
$tagZip=Join-Path $sources 'TagLibSharp-2.3.0-library-source.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if(Test-Path -LiteralPath $tagRoot){
    if(Test-Path -LiteralPath $tagZip){Remove-Item -LiteralPath $tagZip}
    $archive=[IO.Compression.ZipFile]::Open($tagZip,[IO.Compression.ZipArchiveMode]::Create)
    try{
        $files=@(Get-ChildItem -LiteralPath (Join-Path $tagRoot 'src\TaglibSharp') -Recurse -File | Where-Object {$_.FullName -notmatch '\\(bin|obj)\\'})+@(Get-ChildItem -LiteralPath $tagRoot -File)
        foreach($file in $files){$relative=$file.FullName.Substring($tagRoot.Length+1).Replace('\','/');[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal) | Out-Null}
    }finally{$archive.Dispose()}
}else{Copy-Item -LiteralPath (Join-Path $repo 'native\vendor\TagLibSharp-2.3.0-library-source.zip') -Destination $tagZip}
Copy-Item -LiteralPath (Join-Path $repo 'licenses\LGPL-REBUILD.md') -Destination $sources
$glassRoot=Join-Path $repo 'native\vendor\LiquidGlassWinUI'
$glassNotice=Join-Path $output 'LiquidGlassWinUI-1.0.3';New-Item -ItemType Directory -Path $glassNotice -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $glassRoot 'LICENSE'),(Join-Path $glassRoot 'MUYU_INTEGRATION.md') -Destination $glassNotice
