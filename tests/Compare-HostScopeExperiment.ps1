param([Parameter(Mandatory=$true)][string]$Actual)
$ErrorActionPreference='Stop'
$data=Get-Content -LiteralPath $Actual -Raw | ConvertFrom-Json
if(!$data.executed){throw 'Experiment was not executed.'}
foreach($mode in @('HardOnly','HardAndClearance')){
    $runs=@($data.measurements | Where-Object mode -eq $mode)
    $all=@($runs | Where-Object method -eq 'all-octree')
    if($all.Count -ne 1 -or $runs.Count -ne 5){throw "Missing experiment runs for $mode"}
    $expected=@($all[0].results | Where-Object { !$_.LinkInstanceA -or !$_.LinkInstanceB } | ForEach-Object { "$($_.ClashId)|$($_.TestType)|$($_.UnverifiedReason)" } | Sort-Object)
    foreach($run in @($runs | Where-Object method -ne 'all-octree')){
        $found=@($run.results | ForEach-Object { "$($_.ClashId)|$($_.TestType)|$($_.UnverifiedReason)" } | Sort-Object)
        if((Compare-Object $expected $found) -or !$run.match){throw "Host identity/classification mismatch: $mode / $($run.method)"}
        if(@($run.results | Where-Object {$_.LinkInstanceA -and $_.LinkInstanceB}).Count){throw 'Linked-only row in host-centric scan.'}
        if($run.statistics.IncludeLinkToLink -or $run.statistics.TotalSources -ge $all[0].statistics.TotalSources){throw 'Host-centric source count was not reduced.'}
        if($run.method -eq 'host-native' -and $run.statistics.IndexedElements -ge $all[0].statistics.IndexedElements){throw 'Native experiment still indexed the links.'}
        if($mode -eq 'HardOnly' -and $run.statistics.SurfaceDistanceCalls -ne 0){throw 'Hard-only scan performed surface work.'}
    }
}
Write-Output 'PASS: both modes preserve host ClashIds/classifications, reduce sources, exclude linked-only results, and native queries skip link indexing.'
