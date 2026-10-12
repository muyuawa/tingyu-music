param([string]$Source=(Join-Path $PSScriptRoot '..\native\vendor\FFmpeg-330caae0c1acccd2222edc52a05940c574561ce5'))
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
New-Item -ItemType Directory -Path (Join-Path $repo 'artifacts') -Force | Out-Null
$msys=Join-Path (Split-Path $repo) '.tools\msys64\usr\bin\bash.exe'
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation=& $vswhere -latest -products '*' -property installationPath
$vcvars=Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
$environment=& $env:ComSpec /d /c "call `"$vcvars`" >nul && set"
foreach($line in $environment){if($line -match '^([^=]+)=(.*)$'){[Environment]::SetEnvironmentVariable($matches[1],$matches[2],'Process')}}
$env:MSYS2_PATH_TYPE='inherit';$env:CHERE_INVOKING='1';$env:MSYSTEM='MSYS';$env:VSLANG='1033'
$sourcePath=(Resolve-Path -LiteralPath $Source).Path.Replace('\','/')
$prefix=(Join-Path $repo 'native\vendor\ffmpeg-minimal').Replace('\','/')
$link=(Get-Command link.exe).Source.Replace('\','/')
$lib=(Get-Command lib.exe).Source.Replace('\','/')
$compiler=(Get-Command cl.exe).Source.Replace('\','/')
$wrapper=Join-Path $repo 'artifacts\ffmpeg-cl.sh'
$wrapperText=@"
#!/usr/bin/env bash
if [[ `$# -eq 0 || "`$1" == "-nologo-" ]]; then
  printf 'Microsoft (R) C/C++ Optimizing Compiler Version 19.42 for x64\n' >&2
fi
exec '$compiler' "`$@"
"@
[IO.File]::WriteAllText($wrapper,$wrapperText.Replace("`r`n","`n"),[Text.UTF8Encoding]::new($false))
$wrapperPath=$wrapper.Replace('\','/')
$script=@"
set -e
cd '$sourcePath'
chmod +x '$wrapperPath'
./configure --prefix='$prefix' --toolchain=msvc --cc='$wrapperPath' --host-cc='$wrapperPath' --arch=x86_64 --target-os=win32 --ld='$link' --ar='$lib' --disable-autodetect --disable-network --disable-everything --disable-programs --disable-doc --disable-debug --disable-x86asm --disable-static --enable-shared --enable-avcodec --enable-avformat --enable-swresample --enable-avutil --enable-protocol=file --enable-demuxer=aac,ape,flac,mp3,mov,ogg,wav,aiff,wv,dsf,dsdiff --enable-parser=aac,flac,mpegaudio,opus,vorbis --enable-decoder=aac,aac_fixed,alac,ape,flac,mp3,mp3float,opus,vorbis,wavpack,dsd_lsbf,dsd_lsbf_planar,dsd_msbf,dsd_msbf_planar,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be,pcm_f32le,pcm_f32be,pcm_f64le,pcm_f64be,pcm_u8,pcm_s8
make -j6
make install
"@
$scriptPath=Join-Path $repo 'artifacts\build-ffmpeg.sh'
[IO.File]::WriteAllText($scriptPath,$script.Replace("`r`n","`n"),[Text.UTF8Encoding]::new($false))
& $msys --login $scriptPath.Replace('\','/')
if($LASTEXITCODE -ne 0){throw 'Minimal offline FFmpeg build failed'}
