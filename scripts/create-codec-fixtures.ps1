$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$ffmpeg=Join-Path $repo 'native\vendor\ffmpeg\ffmpeg-n8.1-latest-win64-lgpl-shared-8.1\bin\ffmpeg.exe'
$root=Join-Path $repo 'artifacts\test-fixtures'
$inputFile=Join-Path $root 'reference-16.wav'
$cases=@(@('mp3','libmp3lame'),@('m4a','aac'),@('alac.m4a','alac'),@('aiff','pcm_s16be'),@('wv','wavpack'),@('ogg','libvorbis'),@('opus','libopus'),@('float.wav','pcm_f32le'))
foreach($case in $cases){$destination=Join-Path $root ('extended.'+$case[0]);if(Test-Path -LiteralPath $ffmpeg){& $ffmpeg -nostdin -hide_banner -loglevel error -y -i $inputFile -c:a $case[1] $destination;if($LASTEXITCODE -ne 0){throw ('Fixture encoder failed: '+$case[0])}}else{Copy-Item -LiteralPath (Join-Path $repo ('tests\fixtures\extended.'+$case[0])) -Destination $destination}}
