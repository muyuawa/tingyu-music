param(
    [string]$Application=(Join-Path $PSScriptRoot '..\artifacts\queue-scroll-fix\diagnostic\Muyu.App.exe'),
    [string]$Output=(Join-Path $PSScriptRoot '..\artifacts\queue-scroll-fix\verification')
)
$ErrorActionPreference='Stop'
$applicationPath=(Resolve-Path -LiteralPath $Application).Path
$outputPath=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
if(Test-Path -LiteralPath (Join-Path $outputPath 'QUEUE_VERIFICATION.json')){throw 'Use a fresh output directory for each run'}
$process=Start-Process -FilePath $applicationPath -ArgumentList '--capture-ui',('"'+$outputPath+'"'),'--queue-only' -WindowStyle Hidden -PassThru
if(-not $process.WaitForExit(60000)){throw 'Queue verification did not finish within 60 seconds'}
$errorPath=Join-Path $outputPath 'capture-error.txt'
if(Test-Path -LiteralPath $errorPath){throw (Get-Content -LiteralPath $errorPath -Raw)}
$resultPath=Join-Path $outputPath 'QUEUE_VERIFICATION.json'
if(-not (Test-Path -LiteralPath $resultPath)){
    $errorPath=Join-Path $outputPath 'capture-error.txt'
    if(Test-Path -LiteralPath $errorPath){throw (Get-Content -LiteralPath $errorPath -Raw)}
    throw 'Queue verification did not produce a report'
}
$result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
$result.checks | Format-Table name,passed,detail -AutoSize
if(-not $result.passed -or -not $result.completed -or $result.checks.Count -ne 43){throw 'Queue scrolling/cover regression failed or was incomplete'}
Write-Output "PASS: $($result.checks.Count) queue checks; no input injection or audio output"
