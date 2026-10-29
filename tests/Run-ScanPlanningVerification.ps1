param([string]$BuildOutput='', [ValidateSet('Phase12','Phase34','Phase56','Phase78')][string]$Suite='Phase12')
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$phase=if($Suite -eq 'Phase78'){'fullscan-phase78'}elseif($Suite -eq 'Phase56'){'fullscan-phase56'}elseif($Suite -eq 'Phase34'){'fullscan-phase34'}else{'fullscan-phase12'}
if(!$BuildOutput){$BuildOutput=Join-Path $workspace ('artifacts/'+$phase)}
$folder=Join-Path $workspace ('verification/'+$phase+'/native-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $folder | Out-Null
$stage=Join-Path $folder 'addin'
& (Join-Path $workspace 'deploy-clean.ps1') -BuildOutput $BuildOutput -VerificationStage $stage
$deployment=Get-Content (Join-Path $stage 'deployment.json') -Raw | ConvertFrom-Json
$originalHash=(Get-FileHash -LiteralPath $deployment.Manifest).Hash
$temporary=Join-Path $stage 'ClashResolveAI.addin'
$temporaryHash=(Get-FileHash -LiteralPath $temporary).Hash
$environmentNames=@('CLASHRESOLVE_VERIFY_DIR','CLASHRESOLVE_VERIFY_SCANRELEASE','CLASHRESOLVE_VERIFY_SCANESTIMATE','CLASHRESOLVE_VERIFY_SCANFILTER','CLASHRESOLVE_VERIFY_SCANPLAN','CLASHRESOLVE_VERIFY_DASHBOARD','CLASHRESOLVE_VERIFY_MODEL','CLASHRESOLVE_VERIFY_RELEASE','CLASHRESOLVE_VERIFY_PHASE0','CLASHRESOLVE_VERIFY_PHASE5')
$previous=@{};foreach($name in $environmentNames){$previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$replaced=$false
$process=$null
try {
    Copy-Item -LiteralPath $temporary -Destination $deployment.Manifest -Force
    $replaced=$true
    foreach($name in $environmentNames){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    $env:CLASHRESOLVE_VERIFY_DIR=$folder
    if($Suite -eq 'Phase78'){$env:CLASHRESOLVE_VERIFY_SCANRELEASE='1';$env:CLASHRESOLVE_VERIFY_SCANESTIMATE='1'}elseif($Suite -eq 'Phase56'){$env:CLASHRESOLVE_VERIFY_SCANESTIMATE='1'}elseif($Suite -eq 'Phase34'){$env:CLASHRESOLVE_VERIFY_SCANFILTER='1'}else{$env:CLASHRESOLVE_VERIFY_SCANPLAN='1'}
    $process=Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2024\Revit.exe' -WindowStyle Hidden -PassThru
    $process.Id | Set-Content (Join-Path $folder 'process-id.txt')
    $process.StartTime.ToUniversalTime().ToString('o') | Set-Content (Join-Path $folder 'process-start-utc.txt')
    Write-Output "Disposable scan planning verification PID: $($process.Id); evidence: $folder"
    $deadline=(Get-Date).AddMinutes(9)
    while(!(Test-Path (Join-Path $folder 'started.txt'))) {
        if($process.HasExited){throw 'Disposable Revit exited before verification startup.'}
        if((Get-Date) -gt $deadline){throw 'Disposable Revit startup timeout.'}
        Start-Sleep -Milliseconds 500
    }
    if((Get-Content (Join-Path $folder 'started.txt'))[0] -ne $deployment.Assembly){throw 'Unexpected verification assembly.'}
    # Restore registration immediately after verified startup; the test process retains its loaded candidate.
    if((Get-FileHash -LiteralPath $deployment.Manifest).Hash -ne $temporaryHash){throw 'Registration changed externally.'}
    Copy-Item -LiteralPath (Join-Path $deployment.Backup 'original.addin') -Destination $deployment.Manifest -Force
    if((Get-FileHash -LiteralPath $deployment.Manifest).Hash -ne $originalHash){throw 'Original registration restore failed.'}
    $replaced=$false
    'Original registration restored byte-for-byte; installed binaries unchanged.' | Set-Content (Join-Path $folder 'manifest-restored.txt')
    Write-Output 'Candidate startup confirmed; original registration restored.'
    while(!(Test-Path (Join-Path $folder 'complete.txt'))) {
        if(Test-Path (Join-Path $folder 'failed.txt')){throw (Get-Content (Join-Path $folder 'failed.txt') -Raw)}
        if($process.HasExited){throw 'Disposable Revit exited before verification completed.'}
        if((Get-Date) -gt $deadline){throw 'Native verification timeout.'}
        Start-Sleep -Milliseconds 500
    }
    Get-Content (Join-Path $folder 'complete.txt')
    $folder | Set-Content (Join-Path $workspace ('verification/'+$phase+'/native-run-path.txt'))
    # Close only the disposable process created here, after all fixture documents have been closed.
    $process.CloseMainWindow() | Out-Null
} finally {
    foreach($name in $environmentNames){[Environment]::SetEnvironmentVariable($name,$previous[$name],'Process')}
    if($replaced) {
        if((Get-FileHash -LiteralPath $deployment.Manifest).Hash -ne $temporaryHash){throw "Registration changed externally; original backup retained: $($deployment.Backup)"}
        Copy-Item -LiteralPath (Join-Path $deployment.Backup 'original.addin') -Destination $deployment.Manifest -Force
        if((Get-FileHash -LiteralPath $deployment.Manifest).Hash -ne $originalHash){throw 'Original registration restore failed.'}
        'Original registration restored byte-for-byte; installed binaries unchanged.' | Set-Content (Join-Path $folder 'manifest-restored.txt')
    }
}
