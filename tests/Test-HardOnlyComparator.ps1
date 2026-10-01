$ErrorActionPreference='Stop'
$reference=Get-Content (Join-Path $PSScriptRoot 'fixtures\synthetic-baseline.json') -Raw | ConvertFrom-Json
$rows=@($reference.results | Where-Object { $_.TestType -eq 0 -or ($_.TestType -eq 3 -and $_.GeometryEvidence.StartsWith('Boolean intersection failed: ')) })
foreach($row in $rows){
 $row | Add-Member -NotePropertyName UnverifiedReason -NotePropertyValue 1
 if($row.TestType -eq 3){$row.GeometryEvidence='Possible hard: '+$row.GeometryEvidence}
}
$fixture=[pscustomobject]@{results=$rows;statistics=[pscustomobject]@{SurfaceDistanceCalls=0;SurfaceDistanceMilliseconds=0}}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('clash-hard-'+[Guid]::NewGuid().ToString('N')+'.json')
try {
 $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temp
 & (Join-Path $PSScriptRoot 'Compare-HardOnlyBaseline.ps1') -Actual $temp -Baseline (Join-Path $PSScriptRoot 'fixtures\synthetic-baseline.json')
 Write-Output 'PASS hard-only comparator accepts synthetic baseline subset (not geometry evidence)'
 $fixture.statistics.SurfaceDistanceCalls=1
 $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temp
 $rejected=$false
 try{& (Join-Path $PSScriptRoot 'Compare-HardOnlyBaseline.ps1') -Actual $temp -Baseline (Join-Path $PSScriptRoot 'fixtures\synthetic-baseline.json')}catch{$rejected=$_.Exception.Message -like 'Hard-only performed*'}
 if(!$rejected){throw 'Comparator failed to reject surface work'}
 Write-Output 'PASS hard-only comparator rejects surface-distance work'
 $fixture.statistics.SurfaceDistanceCalls=0
 ($rows | Where-Object TestType -eq 3 | Select-Object -First 1).UnverifiedReason=0
 $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temp
 $rejected=$false
 try{& (Join-Path $PSScriptRoot 'Compare-HardOnlyBaseline.ps1') -Actual $temp -Baseline (Join-Path $PSScriptRoot 'fixtures\synthetic-baseline.json')}catch{$rejected=$_.Exception.Message -like 'Wrong possible-hard reason*'}
 if(!$rejected){throw 'Comparator failed to reject wrong unverified reason'}
 Write-Output 'PASS hard-only comparator rejects surface-clearance classification'
} finally {if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp}}
