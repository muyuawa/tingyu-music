param([switch]$SkipNative,[string]$Output=(Join-Path $PSScriptRoot 'artifacts\portable'))
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$portable = [IO.Path]::GetFullPath($Output)
$audioHost = Join-Path $portable 'audio-host'
$localSdk = Join-Path (Split-Path $repo) '.tools\dotnet\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$cmake = if(Test-Path -LiteralPath 'C:\Program Files\CMake\bin\cmake.exe'){'C:\Program Files\CMake\bin\cmake.exe'}else{'cmake'}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $repo
try {
    if (-not $SkipNative) {
        & $cmake -S native -B native/build -G 'Visual Studio 17 2022' -A x64
        if ($LASTEXITCODE -ne 0) { throw 'Native configuration failed' }
        & $cmake --build native/build --config Release --target MuyuAudio -j 6
        if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
    }
    & $sdk publish src/Muyu.AudioHost/Muyu.AudioHost.csproj -c Release -o $audioHost
    if ($LASTEXITCODE -ne 0) { throw 'Audio host build failed' }
    & $sdk publish src/Muyu.App/Muyu.App.csproj -c Release -p:Platform=x64 -o $portable
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed' }
    $glassPins=[ordered]@{}
    foreach($module in 'wuceffectsi.dll','dwmcorei.dll','dcompi.dll','CustomEffectRuntimeNative.dll'){
        $glassPins[$module]=(Get-FileHash -LiteralPath (Join-Path $portable $module) -Algorithm SHA256).Hash
    }
    $glassPins | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $portable 'glass-runtime.json') -Encoding utf8
    $ffmpegBin = Join-Path $repo 'native\vendor\ffmpeg-minimal\bin'
    foreach ($dll in 'avformat-62.dll','avcodec-62.dll','avutil-60.dll','swresample-6.dll') {
        Copy-Item -LiteralPath (Join-Path $ffmpegBin $dll) -Destination $portable
        Copy-Item -LiteralPath (Join-Path $ffmpegBin $dll) -Destination $audioHost
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $installation = & $vswhere -latest -products '*' -property installationPath
        $redistRoot = Join-Path $installation 'VC\Redist\MSVC'
        $crt = Get-ChildItem -LiteralPath $redistRoot -Recurse -Directory | Where-Object { $_.Name -eq 'Microsoft.VC143.CRT' -and $_.FullName -match '\\x64\\' } | Select-Object -First 1
        if ($crt) {
            foreach ($file in Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll') {
                Copy-Item -LiteralPath $file.FullName -Destination $portable
                Copy-Item -LiteralPath $file.FullName -Destination $audioHost
            }
        }
    }
    & (Join-Path $repo 'scripts\collect-notices.ps1') -Destination $portable
    Copy-Item -LiteralPath 'README.md','EXTENSIONS.md' -Destination $portable
    New-Item -ItemType Directory -Path (Join-Path $portable 'examples') -Force | Out-Null
    Copy-Item -LiteralPath 'examples\srt-lyrics-extension.json' -Destination (Join-Path $portable 'examples')
    [IO.File]::WriteAllText((Join-Path $portable 'portable.marker'),'Muyu Music portable data is stored in the adjacent data folder.')
    [xml]$properties=Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props')
    [pscustomobject]@{build=$properties.Project.PropertyGroup.Version;built_at=(Get-Date).ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $portable 'BUILD_INFO.json') -Encoding utf8
    Write-Output (Join-Path $portable 'Muyu.App.exe')
} finally { Pop-Location }
