param([switch]$Native,[switch]$SkipBuild,[string]$BuildOutput='')
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(!$BuildOutput){$BuildOutput=Join-Path $workspace 'artifacts/fullscan-phase78'}
$evidence=Join-Path $workspace ('verification/release-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
if(!$SkipBuild){
    & dotnet build (Join-Path $workspace 'ClashResolveAI.csproj') -c Release -o $BuildOutput --nologo 2>&1 | Tee-Object (Join-Path $evidence 'build.log')
    if($LASTEXITCODE -ne 0){throw 'Production build failed.'}
}
$projects=@('IdentityTests','ScanPlanningHarness/ScanPlanningHarness','LiveMonitorHarness/LiveMonitorHarness','LiveGatewayHarness/LiveGatewayHarness','DashboardHistoryHarness/DashboardHistoryHarness','RadarUiHarness/RadarUiHarness','InspectorHarness/InspectorHarness')
foreach($project in $projects){
    $name=Split-Path $project -Leaf
    & dotnet run --project (Join-Path $workspace ('tests/'+$project+'.csproj')) -c Release 2>&1 | Tee-Object (Join-Path $evidence ($name+'.log'))
    if($LASTEXITCODE -ne 0){throw "$name failed; evidence: $evidence"}
}
if($Native){
    & (Join-Path $workspace 'tests/Run-ScanPlanningVerification.ps1') -Suite Phase78 -BuildOutput $BuildOutput
    if($LASTEXITCODE -ne 0){throw 'Native checks failed.'}
}
Write-Output "Checks passed. Evidence: $evidence"
if(!$Native){Write-Output 'Native Revit UI/geometry and representative loaded production-model performance remain separate release gates.'}
