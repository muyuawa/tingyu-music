param([string]$Probe=(Join-Path $PSScriptRoot '..\artifacts\installer-native\Release\TingyuInstallerProbe.exe'))
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
$fixture=Join-Path $workspace ('uninstall-test-'+[guid]::NewGuid().ToString('N'))
if(-not $fixture.StartsWith($workspace+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture'}
$null=New-Item -ItemType Directory -Path (Join-Path $fixture 'owned\nested'),(Join-Path $fixture 'songs')
Set-Content -LiteralPath (Join-Path $fixture 'owned\nested\settings.json') -Value '{}'
Set-Content -LiteralPath (Join-Path $fixture 'songs\keep.flac') -Value 'original song fixture'
$songHash=(Get-FileHash -LiteralPath (Join-Path $fixture 'songs\keep.flac')).Hash
Set-Content -LiteralPath (Join-Path $fixture 'owned\readonly.txt') -Value 'cache'
(Get-Item -LiteralPath (Join-Path $fixture 'owned\readonly.txt')).IsReadOnly=$true
$null=New-Item -ItemType Junction -Path (Join-Path $fixture 'owned\linked-songs') -Target (Join-Path $fixture 'songs')
& $Probe --test-cleanup ($fixture+'\')
if($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath (Join-Path $fixture 'owned'))){throw 'Cleanup failed'}
if((Get-FileHash -LiteralPath (Join-Path $fixture 'songs\keep.flac')).Hash -ne $songHash){throw 'External songs changed'}
& $Probe --test-cleanup $fixture
if($LASTEXITCODE -ne 0){throw 'Cleanup is not idempotent'}
$null=New-Item -ItemType Directory -Path (Join-Path $fixture 'owned')
$null=New-Item -ItemType Junction -Path (Join-Path $fixture 'linked-parent') -Target $fixture
& $Probe --test-cleanup (Join-Path $fixture 'linked-parent')
if($LASTEXITCODE -eq 0 -or -not (Test-Path -LiteralPath (Join-Path $fixture 'owned'))){throw 'Linked ancestor was not rejected'}
[pscustomobject]@{pass=$true;nested_data_removed=$true;readonly_cache_removed=$true;junction_destinations_preserved=$true;linked_ancestor_rejected=$true;repeated_cleanup_passed=$true;fixture=$fixture}
