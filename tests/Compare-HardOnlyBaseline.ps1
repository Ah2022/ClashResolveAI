param(
    [Parameter(Mandatory=$true)][string]$Actual,
    [Parameter(Mandatory=$true)][string]$Baseline
)
$ErrorActionPreference='Stop'
$saved=Get-Content -LiteralPath $Baseline -Raw | ConvertFrom-Json
$scan=Get-Content -LiteralPath $Actual -Raw | ConvertFrom-Json
$expected=@{}
foreach($row in $saved.results){
    if($row.TestType -eq 0 -or ($row.TestType -eq 3 -and $row.GeometryEvidence.StartsWith('Boolean intersection failed: '))){$expected[$row.ClashId]=$row}
}
$seen=@{}
foreach($row in $scan.results){
    if($seen.ContainsKey($row.ClashId)){throw "Duplicate ClashId: $($row.ClashId)"}
    $seen[$row.ClashId]=$true
    if(!$expected.ContainsKey($row.ClashId)){throw "Unexpected hard-only row: $($row.ClashId)"}
    $reference=$expected[$row.ClashId]
    if($row.TestType -ne $reference.TestType -or $row.OverlapVolumeMM3 -ne $reference.OverlapVolumeMM3){throw "Classification/volume changed: $($row.ClashId)"}
    if($row.TestType -eq 3 -and ($row.UnverifiedReason -ne 1 -or !$row.GeometryEvidence.StartsWith('Possible hard: Boolean intersection failed: '))){throw "Wrong possible-hard reason/evidence: $($row.ClashId)"}
}
if($seen.Count -ne $expected.Count){throw "Missing hard-only results: expected $($expected.Count), actual $($seen.Count)"}
if($null -eq $scan.statistics.SurfaceDistanceCalls -or $scan.statistics.SurfaceDistanceCalls -ne 0 -or $null -eq $scan.statistics.SurfaceDistanceMilliseconds -or $scan.statistics.SurfaceDistanceMilliseconds -ne 0){throw 'Hard-only performed surface-distance work or instrumentation is missing.'}
$hard=@($scan.results | Where-Object TestType -eq 0).Count
$possible=@($scan.results | Where-Object TestType -eq 3).Count
Write-Output "PASS: $hard saved hard ClashIds and $possible saved Boolean-failure ClashIds agree; zero surface-distance calls/time."
