param([string]$BuildOutput=(Join-Path $PSScriptRoot '../artifacts/release'),[ValidateSet('Dashboard','Live','Ledger')][string]$Suite='Dashboard',[switch]$Interactive)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
New-Item -ItemType Directory -Path (Join-Path $workspace 'verification/dashboard-phase67') -Force | Out-Null
$folder=Join-Path $workspace ('verification/'+$Suite.ToLowerInvariant()+'-release-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $folder | Out-Null
$stage=Join-Path $folder 'addin'
& (Join-Path $workspace 'deploy-clean.ps1') -BuildOutput $BuildOutput -VerificationStage $stage
$deployment=Get-Content (Join-Path $stage 'deployment.json') -Raw | ConvertFrom-Json
$originalHash=(Get-FileHash -LiteralPath $deployment.Manifest).Hash
$temporary=Join-Path $stage 'ClashResolveAI.addin'
$temporaryHash=(Get-FileHash -LiteralPath $temporary).Hash
$environmentNames=@('CLASHRESOLVE_VERIFY_DIR','CLASHRESOLVE_VERIFY_DASHBOARD','CLASHRESOLVE_VERIFY_MODEL','CLASHRESOLVE_VERIFY_RELEASE','CLASHRESOLVE_VERIFY_PHASE0','CLASHRESOLVE_VERIFY_PHASE5')
$previous=@{};foreach($name in $environmentNames){$previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$replaced=$false
try {
    Copy-Item -LiteralPath $temporary -Destination $deployment.Manifest -Force;$replaced=$true
    foreach($name in $environmentNames){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    $env:CLASHRESOLVE_VERIFY_DIR=$folder
    if($Suite -eq 'Dashboard'){$env:CLASHRESOLVE_VERIFY_DASHBOARD='1'}elseif($Suite -eq 'Live'){$env:CLASHRESOLVE_VERIFY_RELEASE='1'}else{$env:CLASHRESOLVE_VERIFY_PHASE0='1'}
    $windowStyle=if($Interactive){'Normal'}else{'Hidden'}
    $process=Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2024\Revit.exe' -WindowStyle $windowStyle -PassThru
    $process.Id | Set-Content (Join-Path $folder 'process-id.txt')
    $reference=if($Suite -eq 'Dashboard'){'native-run-path.txt'}elseif($Suite -eq 'Live'){'live-native-run-path.txt'}else{'ledger-native-run-path.txt'}
    $folder | Set-Content (Join-Path $workspace ('verification/dashboard-phase67/'+$reference))
    "Disposable dashboard verification PID: $($process.Id); results: $folder"
    $deadline=(Get-Date).AddMinutes(6)
    while(!(Test-Path (Join-Path $folder 'started.txt'))){if($process.HasExited){throw 'Disposable Revit exited before startup'};if((Get-Date) -gt $deadline){throw 'Disposable startup timeout; registration restored'};Start-Sleep -Seconds 1}
    if((Get-Content (Join-Path $folder 'started.txt'))[0] -ne $deployment.Assembly){throw 'Unexpected loaded assembly'}
    'Candidate assembly startup confirmed.'
} finally {
    foreach($name in $environmentNames){[Environment]::SetEnvironmentVariable($name,$previous[$name],'Process')}
    if($replaced){if((Get-FileHash -LiteralPath $deployment.Manifest).Hash -ne $temporaryHash){throw "Registration changed externally; original retained at $($deployment.Backup)"};Copy-Item -LiteralPath (Join-Path $deployment.Backup 'original.addin') -Destination $deployment.Manifest -Force;if((Get-FileHash -LiteralPath $deployment.Manifest).Hash -ne $originalHash){throw 'Original registration restore failed'};'Original registration restored byte-for-byte; installed binaries unchanged.' | Set-Content (Join-Path $folder 'manifest-restored.txt')}
}
