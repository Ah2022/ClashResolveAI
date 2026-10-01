param([Parameter(Mandatory=$true)][string]$ModelCopy,[Alias('EngineOnly')][switch]$Phase0,[switch]$Phase5)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$allowed=Join-Path $workspace 'verification\production\'
if(![IO.Path]::GetFullPath($ModelCopy).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Use a verification/production copy only.'}
$folder=Join-Path $workspace ('verification\run-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $folder | Out-Null
$stage=Join-Path $folder 'addin'
& (Join-Path $workspace 'deploy-clean.ps1') -VerificationStage $stage
$deployment=Get-Content (Join-Path $stage 'deployment.json') -Raw | ConvertFrom-Json
$originalHash=(Get-FileHash -LiteralPath $deployment.Manifest).Hash
$temporaryManifest=Join-Path $stage 'ClashResolveAI.addin'
$temporaryHash=(Get-FileHash -LiteralPath $temporaryManifest).Hash
$previous=$env:CLASHRESOLVE_VERIFY_DIR
$previousModel=$env:CLASHRESOLVE_VERIFY_MODEL
$previousPhase=$env:CLASHRESOLVE_VERIFY_PHASE0
$previousPhase5=$env:CLASHRESOLVE_VERIFY_PHASE5
$replaced=$false
try {
    # Existing sessions keep their already-loaded assembly. No hot-loading is allowed.
    Copy-Item -LiteralPath $temporaryManifest -Destination $deployment.Manifest -Force
    $replaced=$true
    $env:CLASHRESOLVE_VERIFY_DIR=$folder
    $env:CLASHRESOLVE_VERIFY_MODEL=$ModelCopy
    $env:CLASHRESOLVE_VERIFY_PHASE0=if($Phase0 -or $Phase5){'1'}else{''}
    $env:CLASHRESOLVE_VERIFY_PHASE5=if($Phase5){'1'}else{''}
    $process=Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2024\Revit.exe' -WindowStyle Hidden -PassThru
    $process.Id | Set-Content (Join-Path $folder 'process-id.txt')
    Write-Output "Disposable verification PID: $($process.Id); results: $folder"
    $deadline=(Get-Date).AddMinutes(3)
    while(!(Test-Path (Join-Path $folder 'started.txt'))){
        if($process.HasExited){throw 'Disposable Revit exited before verification startup.'}
        if((Get-Date) -gt $deadline){throw 'Disposable startup timeout. Manifest will be restored.'}
        Start-Sleep -Seconds 1
    }
    $started=Get-Content (Join-Path $folder 'started.txt')
    if($started[0] -ne $deployment.Assembly){throw 'Verification loaded an unexpected assembly.'}
    Write-Output 'Instrumented assembly startup confirmed.'
} finally {
    $env:CLASHRESOLVE_VERIFY_DIR=$previous
    $env:CLASHRESOLVE_VERIFY_MODEL=$previousModel
    $env:CLASHRESOLVE_VERIFY_PHASE0=$previousPhase
    $env:CLASHRESOLVE_VERIFY_PHASE5=$previousPhase5
    if($replaced){
        if((Get-FileHash -LiteralPath $deployment.Manifest).Hash -ne $temporaryHash){throw "Manifest changed externally; original backup retained at $($deployment.Backup)."}
        Copy-Item -LiteralPath (Join-Path $deployment.Backup 'original.addin') -Destination $deployment.Manifest -Force
        if((Get-FileHash -LiteralPath $deployment.Manifest).Hash -ne $originalHash){throw 'Manifest restore hash mismatch.'}
        'Original registration restored byte-for-byte; installed binaries unchanged.' | Set-Content (Join-Path $folder 'manifest-restored.txt')
        Write-Output 'Original manifest restored and hash-verified.'
    }
}

