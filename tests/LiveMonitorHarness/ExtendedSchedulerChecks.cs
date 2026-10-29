using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI;
using ClashResolveAI.Alert;
using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;
using Engine=ClashResolveAI.ClashEngine.ClashEngine;

static class ExtendedSchedulerChecks
{
    public static int Run()
    {
        int passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
        var app=new UIApplication();var doc=app.ActiveUIDocument!.Document;doc.Key="extended-live";DocumentSession.CurrentKey=doc.Key;
        var radar=RadarDataStore.Instance;radar.Activate(doc.Key);radar.Clear();radar.BeginSession();
        var alerts=new AlertSystem();using var scheduler=new LiveScanScheduler(app,alerts);
        Engine.Emit=false;Engine.Infinite=false;Engine.Unreliable=false;Engine.StepDelay=0;
        AppSettings.Current.LiveSliceMilliseconds=5;AppSettings.Current.LiveMaximumSliceMilliseconds=100;
        void Add(int id){doc.Elements[id]=new Element{Id=new(id),UniqueId="e"+id};}
        void Pump(){scheduler.Advance(doc.Key,LiveMonitorService.Instance.Generation);}
        void Drain(int limit=500){for(int i=0;i<limit;i++){Thread.Sleep(30);Pump();if(scheduler.QueueDepth(doc.Key)==0&&scheduler.Status.Outcome==LiveScanOutcome.Completed)return;}throw new Exception("Local queue did not finish: "+scheduler.Status.Reason);}
        for(int i=1;i<=600;i++)Add(i);
        scheduler.Record(doc,doc.Elements.Values.Select(e=>e.Id),LiveChangeKind.Added);Thread.Sleep(320);
        doc.ReadDelay=2;int jobs=Engine.Jobs;Pump();
        Check(Engine.Jobs==jobs&&scheduler.Status.Outcome==LiveScanOutcome.Checking&&scheduler.NeedsPump(doc.Key),"Preparation yields with its detached batch still pumpable before a job exists");
        doc.ReadDelay=0;Drain();
        Check(Engine.Jobs>=jobs+2&&Engine.MaximumSources<=500&&Engine.SeenSources.Contains(600),"A burst larger than one batch drains every source through bounded jobs");
        var metrics=LiveDiagnostics.Current(doc.Key);
        Check(metrics.PreparationMilliseconds>0&&metrics.EnvironmentValidationMilliseconds>0&&metrics.ReconciliationMilliseconds>0,"Live stage timings identify validation, preparation and publication work");
        scheduler.Record(doc,new[]{new ElementId(1)},LiveChangeKind.Modified);Thread.Sleep(320);doc.ReadDelay=120;Pump();
        Check(scheduler.QueueDepth(doc.Key)>0&&scheduler.Status.Outcome==LiveScanOutcome.Checking,"A slow preparation step yields without losing its current batch");
        doc.ReadDelay=0;Thread.Sleep(150);Drain();
        Engine.StepDelay=1100;scheduler.Record(doc,new[]{new ElementId(1)},LiveChangeKind.Modified);Thread.Sleep(320);Pump();
        Check(scheduler.Status.Outcome==LiveScanOutcome.ProtectionPaused,"Hard protection pauses real scheduler progress");
        jobs=Engine.Jobs;Engine.StepDelay=0;scheduler.RequestCheck(doc,new[]{new ElementId(1)});Thread.Sleep(1100);Drain();
        Check(Engine.Jobs==jobs,"Ledger recheck resumes a paused source without creating a duplicate job");
        scheduler.SetMode(doc.Key,MonitorMode.Off);jobs=Engine.Jobs;
        scheduler.Record(doc,new[]{new ElementId(1)},LiveChangeKind.Modified);Pump();
        Check(scheduler.QueueDepth(doc.Key)==0&&Engine.Jobs==jobs,"Off leaves committed changes out of the live queue");
        scheduler.SetMode(doc.Key,MonitorMode.Live);Thread.Sleep(320);Drain();
        Check(Engine.Jobs>jobs,"Returning from Off revalidates tracked sources even without another model edit");
        scheduler.Record(doc,new[]{new ElementId(1)},LiveChangeKind.Modified,true);
        Check(scheduler.Status.Outcome==LiveScanOutcome.RecheckingInputs,"Input events schedule local revalidation without resolving the ledger inline");
        doc.ReadDelay=2;Thread.Sleep(30);int reads=doc.Reads;Pump();
        Check(doc.Reads-reads<20&&scheduler.NeedsPump(doc.Key),"Input refresh resolves a bounded part of the ledger per callback");
        scheduler.Cancel(doc.Key);doc.ReadDelay=0;reads=doc.Reads;Thread.Sleep(110);Pump();
        Check(doc.Reads==reads,"Cancellation also pauses deferred input-refresh work");
        scheduler.RequestCheck(doc,Array.Empty<ElementId>());Thread.Sleep(320);Drain();
        Check(scheduler.Status.Outcome==LiveScanOutcome.Completed,"Explicit recheck drains retained input refresh and scan work");
        // 2,100 former partners used to force the clear-session path. None may
        // disappear, and checking adjacency must not endlessly requeue source 1.
        radar.Clear();Engine.SeenSources.Clear();Engine.MaximumSources=0;Engine.Emit=false;
        var rows=new List<LiveClashDto>();
        for(int i=1000;i<3100;i++){
            Add(i);var row=new ClashResult{ElementA=doc.Elements[1],ElementB=doc.Elements[i],HostDocumentKey=doc.Key,Origin=ResultOrigin.Live,TestType=ClashTestType.HardClash,SessionGeneration=LiveMonitorService.Instance.Generation};
            rows.Add(LiveClashResolver.Capture(doc,row,LiveEnvironment.Capture(doc)));
        }
        radar.ReplaceSnapshot(rows);ScanCoordinator.Revisions.Changed(doc.Key,new long[]{1},false);
        scheduler.Record(doc,new[]{new ElementId(1)},LiveChangeKind.Modified);Thread.Sleep(320);Drain();
        Check(Engine.SeenSources.Contains(3099)&&Engine.MaximumSources<=500&&scheduler.QueueDepth(doc.Key)==0,"Oversized prior-pair dependencies finish in smaller jobs without clearing or cycling");
        Check(radar.ActiveCount==0,"Reliable dependency batches resolve former clashes only after their coverage is checked");
        Engine.Emit=true;DocumentSession.CurrentKey="host";radar.Activate("host");
        return passed;
    }
}
