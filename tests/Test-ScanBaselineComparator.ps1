param()
$ErrorActionPreference='Stop'
$reference=Join-Path $PSScriptRoot 'fixtures\synthetic-baseline.json'
$fixture=Get-Content -LiteralPath $reference -Raw | ConvertFrom-Json
foreach($name in @('CollectIndexMilliseconds','CandidateMilliseconds','GeometryMilliseconds','BooleanMilliseconds','SurfaceDistanceMilliseconds','BooleanTests','SurfaceDistanceCalls')) {
    $fixture.statistics | Add-Member -NotePropertyName $name -NotePropertyValue 0
}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('clash-compare-'+[Guid]::NewGuid().ToString('N')+'.json')
try {
    $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temp
    & (Join-Path $PSScriptRoot 'Compare-ScanBaseline.ps1') -Actual $temp -Baseline (Join-Path $PSScriptRoot 'fixtures\synthetic-baseline.json')
    Write-Output 'PASS comparator accepts equivalent synthetic fixture (not a Revit measurement)'
    $fixture.results[0].TestType=99
    $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temp
    $rejected=$false
    try { & (Join-Path $PSScriptRoot 'Compare-ScanBaseline.ps1') -Actual $temp -Baseline (Join-Path $PSScriptRoot 'fixtures\synthetic-baseline.json') } catch { $rejected=$_.Exception.Message -like 'Mismatch:*TestType' }
    if(!$rejected){throw 'Comparator failed to reject classification change'}
    Write-Output 'PASS comparator rejects classification drift'
    $fixture.results[1].ClashId=$fixture.results[0].ClashId
    $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temp
    $rejected=$false
    try { & (Join-Path $PSScriptRoot 'Compare-ScanBaseline.ps1') -Actual $temp -Baseline (Join-Path $PSScriptRoot 'fixtures\synthetic-baseline.json') } catch { $rejected=$_.Exception.Message -like 'Duplicate ClashId:*' }
    if(!$rejected){throw 'Comparator failed to reject duplicate identity'}
    Write-Output 'PASS comparator rejects duplicate identity'
} finally { if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp} }
