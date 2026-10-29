param([switch]$Interactive)
$ErrorActionPreference='Stop'
if (@(Get-Process Revit -ErrorAction SilentlyContinue).Count) { throw 'Close Revit before starting the disposable release verification.' }
$taskWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskFolder=Join-Path $taskWorkspace ('verification\release94-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $taskFolder | Out-Null
$taskPrevious=$env:CLASHRESOLVE_VERIFY_DIR
$taskReleasePrevious=$env:CLASHRESOLVE_VERIFY_RELEASE
try {
    $env:CLASHRESOLVE_VERIFY_DIR=$taskFolder
    $env:CLASHRESOLVE_VERIFY_RELEASE='1'
    $taskWindow=if($Interactive){'Normal'}else{'Hidden'}
    $taskProcess=Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2024\Revit.exe' -WindowStyle $taskWindow -PassThru
    $taskProcess.Id | Set-Content (Join-Path $taskFolder 'process-id.txt')
    Write-Output "Disposable Revit PID: $($taskProcess.Id); results: $taskFolder"
} finally { $env:CLASHRESOLVE_VERIFY_DIR=$taskPrevious; $env:CLASHRESOLVE_VERIFY_RELEASE=$taskReleasePrevious }
