param([Parameter(Mandatory=$true)][string]$RunFolder)
$ErrorActionPreference='Stop'
$data=Get-Content -LiteralPath (Join-Path $RunFolder 'phase4-ledger-scan.json') -Raw | ConvertFrom-Json
if($data.ledgerCount -lt 1 -or $data.statistics.Sources -ne $data.ledgerCount){throw 'Re-check source count differs from the ledger.'}
if($data.statistics.IndexedElements -ne 0 -or $data.statistics.IncludeLinkToLink){throw 'Ledger re-check indexed a model or enabled linked sources.'}
if(@($data.placementEvents).Count -ne 2){throw 'Separate placement transactions were not observed.'}
$checks=Get-Content -LiteralPath (Join-Path $RunFolder 'results.txt')
if(@($checks | Where-Object {$_ -like 'FAIL *'}).Count -gt 0){throw 'Revit acceptance checks failed.'}
if(!(Test-Path -LiteralPath (Join-Path $RunFolder 'complete.txt'))){throw 'Verification did not complete.'}
Write-Output "PASS: $($data.ledgerCount) ledger sources, zero indexed elements, separate pipe placement events, and completed Revit acceptance checks."
