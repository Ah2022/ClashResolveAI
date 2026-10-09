using ClashResolveAI.LiveMonitor;
static class LiveDiagnosticsChecks
{
    public static void Run(Action<bool,string> check)
    {
        string key="diagnostic-fixture";var changes=new ChangeAccumulator();var now=DateTime.UtcNow;
        changes.Record(key,1,"fixture",LiveChangeKind.Modified,now);
        var batch=changes.Detach(key,7,now.AddMilliseconds(500));
        LiveDiagnostics.Batch(batch,MonitorMode.Live,12,"fingerprint",1);LiveDiagnostics.Started(key);
        var started=LiveDiagnostics.Current(key).ScanStartedUtc;LiveDiagnostics.Started(key);
        check(LiveDiagnostics.Current(key).ScanStartedUtc==started&&LiveDiagnostics.Export(key).Count(s=>s.Event=="ScanStarted")==1,"Resuming a batch preserves its start timestamp without repeated start events");
        LiveDiagnostics.Stage(key,"Preparation",12);LiveDiagnostics.Stage(key,"Preparation",8);LiveDiagnostics.Stage(key,"Environment",30);
        LiveDiagnostics.Stage(key,"DtoCapture",4);LiveDiagnostics.Stage(key,"Reconciliation",6);LiveDiagnostics.Stage(key,"InputRefresh",3);
        LiveDiagnostics.EngineTimings(key,2,10,11,5,7);
        LiveDiagnostics.Metrics(key,20,10,1,2,3,DateTime.UtcNow);LiveDiagnostics.Slice(key,35,1);
        LiveDiagnostics.Finish(key,"Completed","Verified",0,4,1,true,true);
        var snapshot=LiveDiagnostics.Current(key);
        check(snapshot.PreparationMilliseconds==20&&snapshot.EnvironmentValidationMilliseconds==30&&snapshot.DtoCaptureMilliseconds==4&&snapshot.ReconciliationMilliseconds==6&&snapshot.InputRefreshMilliseconds==3&&snapshot.MaximumStageMilliseconds==30&&snapshot.SlowestStage=="Environment","Diagnostics accumulate live stages and identify the slowest callback stage");
        check(snapshot.EngineCollectionMilliseconds==2&&snapshot.CandidateMilliseconds==10&&snapshot.GeometryMilliseconds==11&&snapshot.BooleanMilliseconds==5&&snapshot.SurfaceDistanceMilliseconds==7,"Live diagnostics copy existing engine timings without changing the engine");
        check(snapshot.DocumentChangedUtc==now&&snapshot.BatchCreatedUtc==batch.CreatedUtc&&snapshot.ScanStartedUtc!=null&&snapshot.FirstResultUtc!=null&&snapshot.ScanCompletedUtc!=null&&snapshot.FirstPublishedResultUtc!=null,"Diagnostics correlate all change/batch/scan/result timestamps");
        check(snapshot.BatchId==batch.BatchId&&snapshot.SessionGeneration==7&&snapshot.GeometryRevision==12&&snapshot.ChangedElementCount==1,"Diagnostics preserve document, generation, revision and batch identity");
        check(snapshot.CandidateCount==20&&snapshot.TestedPairCount==10&&snapshot.BooleanFailures==1&&snapshot.NewClashCount==4&&snapshot.ResolvedClashCount==1&&snapshot.MaximumApiSliceMilliseconds==35,"Diagnostics capture candidate, pair, failure, result and maximum-slice counters");
        LiveDiagnostics.Request(key,7,Guid.NewGuid(),"Inspect",2,3,true);LiveDiagnostics.Retry(key);
        var changed=LiveDiagnostics.Current(key);
        check(changed.StaleRequestCount==1&&changed.CoalescedRequestCount==3&&changed.WakeRetryCount==1&&changed.RequestQueueDepth==2,"Diagnostics count stale requests, coalescing, retries and request depth");
        check(snapshot.StaleRequestCount==0&&snapshot.RequestQueueDepth==0,"Previously exported diagnostic snapshots remain immutable");
        Action<LiveDiagnosticSnapshot> fail=_=>throw new Exception("sink failure");LiveDiagnostics.Recorded+=fail;
        LiveDiagnostics.Status(key,MonitorMode.Trigger,new LiveScanStatus(LiveScanOutcome.Watching,"Waiting",1));LiveDiagnostics.Recorded-=fail;
        check(LiveDiagnostics.Current(key).Mode==MonitorMode.Trigger,"A failing diagnostic sink cannot break monitor state updates");
        for(int i=0;i<600;i++)LiveDiagnostics.Status(key,MonitorMode.Trigger,new LiveScanStatus(LiveScanOutcome.Watching,"Waiting",i));
        check(LiveDiagnostics.Export(key).Count<=LiveDiagnostics.HistoryCapacity,"Diagnostic history is bounded instead of retaining an unlimited session");
        check(typeof(LiveDiagnosticSnapshot).GetProperties().All(p=>p.SetMethod==null)&&typeof(LiveDiagnosticSnapshot).GetProperties().All(p=>!(p.PropertyType.FullName??"").StartsWith("Autodesk.")),"Diagnostic exports contain immutable plain values without Revit types");
        LiveDiagnostics.Close(key);
    }
}
