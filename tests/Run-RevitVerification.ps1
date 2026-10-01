param([string]$ModelCopy = "")
$ErrorActionPreference = 'Stop'
if (@(Get-Process Revit -ErrorAction SilentlyContinue).Count) { throw 'Close Revit first. Verification uses its own disposable project.' }
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$folder = Join-Path $workspace ('verification\run-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$previous = $env:CLASHRESOLVE_VERIFY_DIR
$previousModel = $env:CLASHRESOLVE_VERIFY_MODEL
try {
    $env:CLASHRESOLVE_VERIFY_DIR = $folder
    $env:CLASHRESOLVE_VERIFY_MODEL = $ModelCopy
    $process = Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2024\Revit.exe' -WindowStyle Hidden -PassThru
    $process.Id | Set-Content (Join-Path $folder 'process-id.txt')
    Write-Output "Verification PID: $($process.Id)"
    Write-Output "Results: $folder"
} finally { $env:CLASHRESOLVE_VERIFY_DIR = $previous; $env:CLASHRESOLVE_VERIFY_MODEL = $previousModel }
