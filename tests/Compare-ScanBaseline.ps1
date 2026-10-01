param(
    [Parameter(Mandatory=$true)][string]$Actual,
    [Parameter(Mandatory=$true)][string]$Baseline
)
$ErrorActionPreference = 'Stop'
$expected = Get-Content -LiteralPath $Baseline -Raw | ConvertFrom-Json
$current = Get-Content -LiteralPath $Actual -Raw | ConvertFrom-Json
$fields = @('ElementAId','ElementBId','LinkInstanceA','LinkInstanceB','TestType','Severity','GeometryEvidence','GapMM','OverlapVolumeMM3','RequiredClearanceMM')
$byId = @{}
foreach ($row in $current.results) {
    if ($byId.ContainsKey($row.ClashId)) { throw "Duplicate ClashId: $($row.ClashId)" }
    $byId[$row.ClashId] = $row
}
if ($expected.results.Count -ne $byId.Count) { throw "Result count differs: $($expected.results.Count) vs $($byId.Count)" }
foreach ($row in $expected.results) {
    if (!$byId.ContainsKey($row.ClashId)) { throw "Missing ClashId: $($row.ClashId)" }
    foreach ($field in $fields) {
        if ($row.$field -cne $byId[$row.ClashId].$field) { throw "Mismatch: $($row.ClashId) / $field" }
    }
}
foreach ($field in @('CollectIndexMilliseconds','CandidateMilliseconds','GeometryMilliseconds','BooleanMilliseconds','SurfaceDistanceMilliseconds','BooleanTests','SurfaceDistanceCalls')) {
    if ($null -eq $current.statistics.$field -or $current.statistics.$field -lt 0) { throw "Missing or invalid instrumentation: $field" }
}
Write-Output "PASS: $($byId.Count) ClashIds and all baseline result fields agree; phase instrumentation present."
