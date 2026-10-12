param([string]$Source=(Join-Path $PSScriptRoot 'artifacts\portable'))
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$portableRoot=(Resolve-Path -LiteralPath $Source).Path
$localSdk = Join-Path (Split-Path $repo) '.tools\dotnet\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $repo
try {
    $nativeOutput = & .\native\build\Release\MuyuCodecTests.exe (Join-Path $repo 'artifacts\test-fixtures')
    if ($LASTEXITCODE -ne 0) { throw "Codec tests failed: $nativeOutput" }
    $nativeOutput | Write-Output
    & (Join-Path $repo 'scripts\create-codec-fixtures.ps1')
    & $sdk build tests/Muyu.Tests/Muyu.Tests.csproj -c Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Managed test build failed' }
    $testAssembly = Join-Path $repo 'tests\Muyu.Tests\bin\x64\Release\net8.0\win-x64\Muyu.Tests.dll'
    $managedOutput = & $sdk $testAssembly (Join-Path $repo 'artifacts\test-fixtures') (Join-Path $portableRoot 'audio-host\Muyu.AudioHost.exe') (Join-Path $portableRoot 'Muyu.App.dll')
    if ($LASTEXITCODE -ne 0) { throw "Managed tests failed: $managedOutput" }
    $managedOutput | Write-Output
    $required = @('Muyu.App.exe','Muyu.App.dll','MuyuAudio.dll','Microsoft.UI.Xaml.dll','Microsoft.Graphics.Canvas.dll','e_sqlite3.dll','vcruntime140.dll','audio-host\Muyu.AudioHost.exe','audio-host\MuyuAudio.dll','TagLibSharp.dll','audio-host\NAudio.Asio.dll','avcodec-62.dll','avformat-62.dll','avutil-60.dll','swresample-6.dll','audio-host\avcodec-62.dll','audio-host\avformat-62.dll','audio-host\avutil-60.dll','audio-host\swresample-6.dll','licenses\libFLAC.txt','licenses\sources\ffmpeg-minimal-source.tar.gz','licenses\sources\TagLibSharp-2.3.0-library-source.zip','README.md','IMPLEMENTATION_STATUS.md','EXTENSIONS.md','examples\srt-lyrics-extension.json','portable.marker')
    foreach ($path in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $portableRoot $path))) { throw "Missing packaged dependency: $path" }
    }
    [xml]$buildProperties = Get-Content -LiteralPath 'Directory.Build.props'
    $binaryHashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $portableRoot -Recurse -File | Where-Object {$_.Extension -in '.dll','.exe'} | Sort-Object FullName) {
        $binary = $file.FullName.Substring($portableRoot.Length+1)
        $binaryHashes[$binary] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    if ($binaryHashes['MuyuAudio.dll'] -ne $binaryHashes['audio-host\MuyuAudio.dll']) { throw 'Native libraries differ between app and host' }
    $testedCore = (Get-FileHash -LiteralPath 'tests\Muyu.Tests\bin\x64\Release\net8.0\win-x64\Muyu.Core.dll' -Algorithm SHA256).Hash
    if ($testedCore -ne $binaryHashes['Muyu.Core.dll']) { throw 'Packaged application core differs from tested core. Run build.ps1 again.' }
    foreach ($assembly in @('Muyu.App.dll','Muyu.Core.dll','audio-host\Muyu.AudioHost.dll','audio-host\Muyu.Core.dll')) {
        $version = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $portableRoot $assembly)).Version
        if ($version.ToString() -ne $buildProperties.Project.PropertyGroup.AssemblyVersion) { throw "Wrong packaged version: $assembly" }
    }
    [pscustomobject]@{
        date = (Get-Date -Format 'yyyy-MM-dd')
        build = $buildProperties.Project.PropertyGroup.Version
        native = @($nativeOutput)
        managed = @($managedOutput)
        gui_tested = $false
        audio_output_opened = $false
        hardware_tested = $false
        required_files = $required
        application_sha256 = (Get-FileHash -LiteralPath (Join-Path $portableRoot 'Muyu.App.dll') -Algorithm SHA256).Hash
        native_sha256 = (Get-FileHash -LiteralPath (Join-Path $portableRoot 'MuyuAudio.dll') -Algorithm SHA256).Hash
        tested_core_sha256 = $testedCore
        test_assembly_sha256 = (Get-FileHash -LiteralPath $testAssembly -Algorithm SHA256).Hash
        binaries = $binaryHashes
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $portableRoot 'VERIFICATION.json') -Encoding utf8
} finally { Pop-Location }
