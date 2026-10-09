using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI;
using ClashResolveAI.Alert;
using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;
using Engine=ClashResolveAI.ClashEngine.ClashEngine;

try
{
if(args.Contains("--verify-failure-exit"))throw new InvalidOperationException("Intentional failure to verify the harness exits cleanly");
int passed=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
var app=new UIApplication();var doc=app.ActiveUIDocument!.Document;
doc.Elements[1]=new Element{Id=new(1),UniqueId="A"};doc.Elements[2]=new Element{Id=new(2),UniqueId="B"};
var radar=RadarDataStore.Instance;radar.Activate(doc.Key);radar.Clear();radar.BeginSession();
var alert=new AlertSystem();using var scheduler=new LiveScanScheduler(app,alert);
LiveMonitorService.Instance.OnRenew=scheduler.SessionRenewed;
void Record(long id,LiveChangeKind kind=LiveChangeKind.Modified,bool global=false){ScanCoordinator.Revisions.Changed(doc.Key,new[]{id},global);scheduler.Record(doc,new[]{new ElementId(id)},kind,global);}
void Pump(){scheduler.Advance(doc.Key,LiveMonitorService.Instance.Generation);}
void Ready(){Thread.Sleep(320);Pump();for(int i=0;i<30&&!Engine.Infinite&&(scheduler.Status.Outcome==LiveScanOutcome.Checking||scheduler.Status.Outcome==LiveScanOutcome.RecheckingInputs);i++){Thread.Sleep(30);Pump();}}
Record(1,LiveChangeKind.Added);Ready();
Check(radar.ActiveCount==1&&scheduler.QueueDepth(doc.Key)==0&&scheduler.Status.Outcome==LiveScanOutcome.Completed,"Scheduler publishes detached DTOs and acknowledges a completed local batch");
var dto=radar.GetActive().Single();var resolved=LiveClashResolver.Resolve(doc,dto);
Check(resolved.ElementA.UniqueId=="A"&&resolved.ElementB.UniqueId=="B","API resolver resolves both endpoint UniqueIds");
bool Reject(Action action){try{action();return false;}catch(InvalidOperationException){return true;}}
doc.Elements[1].VersionGuid=Guid.NewGuid();
Check(Reject(()=>LiveClashResolver.Resolve(doc,dto)),"Endpoint geometry version change rejects an old DTO without relying on numeric IDs");
Record(1);Ready();dto=radar.GetActive().Single();
var old=doc.Elements[1];doc.Elements[1]=new Element{Id=new(1),UniqueId="replacement"};
Check(Reject(()=>LiveClashResolver.Resolve(doc,dto)),"Reused numeric IDs cannot resolve an old endpoint UniqueId");doc.Elements[1]=old;
ClashResolveAI.Rules.RulesEngine.Contents="changed rules";
Check(Reject(()=>LiveClashResolver.Resolve(doc,dto)),"Changed rule contents reject otherwise unchanged endpoint geometry");
Thread.Sleep(1050);Pump();Ready();
Check(scheduler.Status.Outcome==LiveScanOutcome.Completed&&radar.GetActive().Single().EnvironmentStamp==LiveEnvironment.Capture(doc),"Changed rules automatically recheck monitored sources without Full Scan");
Record(1,LiveChangeKind.Modified,true);Ready();
Check(!scheduler.Status.RequiresFullScan&&scheduler.QueueDepth(doc.Key)==0&&ScanCoordinator.InputsStale,"Live publishes current clashes while Full Scan inputs remain stale");
int jobs=Engine.Jobs;
scheduler.Clear(doc.Key);scheduler.RequestCheck(doc,new[]{new ElementId(1)});Ready();
Check(scheduler.Status.Outcome==LiveScanOutcome.Completed,"A cleared live session can recheck independently of Full Scan");
Engine.Infinite=true;Record(1);Ready();Check(scheduler.QueueDepth(doc.Key)>0,"An in-flight sliced scan retains batch ownership");
int beforeDispose=Engine.Disposed;Record(1);Record(2);
Check(Engine.Disposed>beforeDispose&&scheduler.QueueDepth(doc.Key)==2,"Mid-scan edits dispose the old job and preserve later changes including the same ID");
scheduler.Cancel(doc.Key);Engine.Infinite=false;Ready();jobs=Engine.Jobs;
Check(scheduler.Status.Outcome==LiveScanOutcome.Cancelled&&scheduler.QueueDepth(doc.Key)==2,"Cancel preserves pending edits and pauses automatic advancement");
scheduler.RequestCheck(doc,Array.Empty<ElementId>());Ready();Check(Engine.Jobs>jobs&&scheduler.QueueDepth(doc.Key)==0,"Explicit recheck resumes retained changes after cancellation");
Engine.StepDelay=850;AppSettings.Current.LiveMaximumSliceMilliseconds=100;jobs=Engine.Jobs;Record(1);Ready();
Check(scheduler.Status.Outcome==LiveScanOutcome.Completed&&scheduler.QueueDepth(doc.Key)==0&&Engine.Jobs==jobs+1,"A slow native slice retains its iterator and publishes without restarting the batch");
Engine.StepDelay=1100;Record(1);Ready();
Check(scheduler.Status.Outcome==LiveScanOutcome.ProtectionPaused&&scheduler.QueueDepth(doc.Key)>0,"An excessive native slice pauses with its job and batch retained");
jobs=Engine.Jobs;Ready();Check(Engine.Jobs==jobs,"Hard responsiveness protection prevents automatic retry");
Engine.StepDelay=0;AppSettings.Current.LiveMaximumSliceMilliseconds=100;scheduler.RequestCheck(doc,Array.Empty<ElementId>());Thread.Sleep(1100);Ready();
Check(scheduler.Status.Outcome==LiveScanOutcome.Completed&&scheduler.QueueDepth(doc.Key)==0&&Engine.Jobs==jobs,"Explicit recheck continues the retained job after a hard pause");
Engine.Unreliable=true;Record(1);Ready();
Check(radar.ActiveCount==1&&radar.GetActive().Single().Verification==LiveVerificationState.Unverified&&scheduler.QueueDepth(doc.Key)>0,"Missing geometry retains an unverified clash and unchecked source rather than resolving it");
Engine.Unreliable=false;scheduler.RequestCheck(doc,new[]{new ElementId(1)});Ready();
Engine.Emit=false;Record(1);Ready();
Check(radar.ActiveCount==0&&scheduler.QueueDepth(doc.Key)==0,"Reliable empty local results resolve previous overlaps after movement");
Engine.Emit=true;Record(1);Ready();dto=radar.GetActive().Single();
doc.Elements.Remove(1);Record(1,LiveChangeKind.Deleted);Ready();
Check(radar.ActiveCount==0,"Confirmed host UniqueId deletion resolves its historical clashes");
doc.Elements[1]=old;Record(1,LiveChangeKind.Added);Ready();
Check(radar.ActiveCount==1,"Restoring the same UniqueId after Undo/Redo reactivates the stable pair");
var linked=new Document{Key="linked"};linked.Elements[7]=new Element{Id=new(7),UniqueId="linked-A"};
var link=new RevitLinkInstance{Id=new(9),UniqueId="link-instance",Linked=linked};doc.Elements[9]=link;
var row=new ClashResult{ElementA=doc.Elements[1],ElementB=linked.Elements[7],HostDocumentKey=doc.Key,LinkInstanceB=link.UniqueId,GeometryRevision=ScanCoordinator.DocumentRevision(doc.Key),Origin=ResultOrigin.Live,SessionGeneration=LiveMonitorService.Instance.Generation};
var linkedDto=LiveClashResolver.Capture(doc,row,LiveEnvironment.Capture(doc));
Check(LiveClashResolver.Resolve(doc,linkedDto).ElementB.UniqueId=="linked-A","Loaded-link endpoints resolve inside their specific link instance");
link.Transform.Origin=new XYZ(10,0,0);Check(Reject(()=>LiveClashResolver.Resolve(doc,linkedDto)),"Link transform changes reject old navigation/inspection DTOs");link.Transform.Origin=XYZ.Zero;
link.Linked=null;Check(Reject(()=>LiveClashResolver.Resolve(doc,linkedDto)),"Unloaded links reject stale requests instead of impersonating deletion");link.Linked=linked;
link.Linked=new Document{Key="replacement-linked"};Check(Reject(()=>LiveClashResolver.Resolve(doc,linkedDto)),"Reloading a different linked document invalidates the captured link identity");
link.Linked=linked;
scheduler.RequestCheck(doc,new[]{new ElementId(1)});Ready();
scheduler.SetMode(doc.Key,MonitorMode.Trigger);jobs=Engine.Jobs;Record(1);Ready();
Check(Engine.Jobs==jobs&&scheduler.QueueDepth(doc.Key)==1,"Trigger accumulates changes without automatically creating a scan");
scheduler.RequestCheck(doc,Array.Empty<ElementId>());Ready();
Check(Engine.Jobs>jobs&&scheduler.QueueDepth(doc.Key)==0,"Trigger Check Changes drains its requested work");
jobs=Engine.Jobs;Record(1);Ready();
Check(Engine.Jobs==jobs&&scheduler.QueueDepth(doc.Key)==1,"Changes after a completed Trigger request wait for another click");
scheduler.RequestCheck(doc,Array.Empty<ElementId>());Engine.Infinite=true;Ready();Record(1);Engine.Infinite=false;Ready();
Check(scheduler.QueueDepth(doc.Key)==1&&scheduler.Status.Outcome!=LiveScanOutcome.Checking,"Edits after a Trigger watermark invalidate its batch and wait for a fresh click");
scheduler.SetMode(doc.Key,MonitorMode.Off);jobs=Engine.Jobs;Record(1);scheduler.RequestCheck(doc,new[]{new ElementId(2)});Ready();
Check(scheduler.QueueDepth(doc.Key)==0&&Engine.Jobs==jobs&&!scheduler.NeedsPump(doc.Key),"Off clears pending work and ignores event, check, and timer scan requests");
scheduler.SetMode(doc.Key,MonitorMode.Live);Record(1);Ready();
Check(Engine.Jobs>jobs&&scheduler.QueueDepth(doc.Key)==0,"Returning to Live resumes automatic checking of subsequent changes");
ClashResolveAI.Rules.RulesEngine.CaptureDelay=150;
Thread.Sleep(1050);Record(1);Ready();
Check(scheduler.QueueDepth(doc.Key)==0&&scheduler.Status.Outcome==LiveScanOutcome.Completed,"Input validation longer than the scan target does not starve geometry or DTO publication");
ClashResolveAI.Rules.RulesEngine.CaptureDelay=0;
passed+=ViewModelChecks.Run(radar.GetActive().Single());
Engine.Infinite=true;Record(1);Ready();
ClashResolveAI.Rules.RulesEngine.Contents="changed while scanning";Engine.Infinite=false;Thread.Sleep(30);Pump();
Check(scheduler.Status.Outcome==LiveScanOutcome.RecheckingInputs&&scheduler.QueueDepth(doc.Key)>0&&radar.GetActive().All(c=>c.Verification!=LiveVerificationState.Verified),"Publication rejects changed inputs and queues a local retry without Full Scan");
Ready();
Check(scheduler.Status.Outcome==LiveScanOutcome.Completed&&radar.GetActive().Single().EnvironmentStamp==LiveEnvironment.Capture(doc),"Local retry publishes only the current environment");
scheduler.SetMode(doc.Key,MonitorMode.Trigger);jobs=Engine.Jobs;
ClashResolveAI.Rules.RulesEngine.Contents="trigger rules changed";Thread.Sleep(1050);Pump();Ready();
Check(Engine.Jobs==jobs&&scheduler.QueueDepth(doc.Key)>0&&scheduler.Status.Outcome==LiveScanOutcome.RecheckingInputs,"Trigger input refresh waits for Check Changes, never Full Scan");
scheduler.RequestCheck(doc,Array.Empty<ElementId>());Ready();
Check(Engine.Jobs>jobs&&scheduler.Status.Outcome==LiveScanOutcome.Completed,"Trigger checks refreshed inputs without a Full Scan prerequisite");
var beforeFullSetting=LiveEnvironment.Capture(doc);AppSettings.Current.FullScanMode=ScanMode.HardAndClearance;
Check(beforeFullSetting==LiveEnvironment.Capture(doc),"Full Scan mode changes do not invalidate live environments");
passed+=ExtendedSchedulerChecks.Run();
Console.WriteLine($"{passed} scheduler/resolver/view-model checks passed.");
}
catch(Exception ex)
{
    Console.Error.WriteLine("FAIL LiveMonitorHarness: "+ex);
    Environment.ExitCode=1;
}
