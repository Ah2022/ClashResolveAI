// Deterministic API/dispatcher stand-ins: verifies gateway dispatch, not Revit runtime behavior.
namespace Autodesk.Revit.DB
{
    public sealed class Document {public string Key="A";public bool IsValidObject=true,IsFamilyDocument,IsLinked;}
}
namespace Autodesk.Revit.UI
{
    public interface IExternalEventHandler {void Execute(UIApplication app);string GetName();}
    public sealed class UIDocument {public Autodesk.Revit.DB.Document Document=new();}
    public sealed class UIApplication {public UIDocument? ActiveUIDocument=new();}
    public enum ExternalEventRequest {Accepted,Pending,Denied,TimedOut}
    public sealed class ExternalEvent : IDisposable {
        public static int Raises;
        public static ExternalEventRequest Result=ExternalEventRequest.Accepted;
        public static ExternalEvent Create(IExternalEventHandler handler)=>new();
        public ExternalEventRequest Raise(){Raises++;return Result;}
        public void Dispose(){}
    }
}
namespace System.Windows.Threading
{
    public sealed class DispatcherTimer {
        public static DispatcherTimer? Latest;
        public TimeSpan Interval {get;set;}
        public event EventHandler? Tick;
        public bool Started;
        public DispatcherTimer(){Latest=this;}
        public void Start(){Started=true;}
        public void Stop(){Started=false;}
        public void Fire(){if(Started)Tick?.Invoke(this,EventArgs.Empty);}
    }
}
namespace ClashResolveAI.Core
{
    public static class Diagnostics {public static void Log(string message,Exception? ex=null){} }
    public static class DocumentSession {public static string Key(Autodesk.Revit.DB.Document doc)=>doc.Key;}
    public class ClashResult {
        public string HostDocumentKey="A",NormalizedKey="pair";public long ElementAId=1,ElementBId=2;
        public long GeometryRevision=3;
        public bool Stale;
    }
    public static class ScanCoordinator {public static bool IsStale(ClashResult clash)=>clash.Stale;}
}
namespace ClashResolveAI.Inspection
{
    public sealed class InspectorPreferences {public double PaddingFeet;public bool Focus,Context,AxisB;public int Corner;}
    public sealed class InspectionScene {public string? Project,Notice;public long IssueId;public int Min,Max,Clash,AxisA,AxisB;public double OverlapVolume;}
    public static class InspectionHandler {
        public static int Calls;
        public static Action? BeforePublish;
        public static void Run(Autodesk.Revit.UI.UIApplication app,ClashResolveAI.Core.ClashResult clash,InspectionScene? scene,InspectorPreferences? p,Action<InspectionScene?,string> completed){Calls++;BeforePublish?.Invoke();completed(new(),"");}
    }
}
namespace ClashResolveAI.Services
{
    public static class ClashViewNavigation {
        public static int Calls;
        public static Func<bool>? DeferredValid;
        public static long Show(Autodesk.Revit.UI.UIApplication app,ClashResolveAI.Core.ClashResult clash,bool threeD,Func<bool>? valid=null){Calls++;DeferredValid=valid;return 1;}
    }
}
namespace ClashResolveAI.LiveMonitor
{
    public enum LiveVerificationState {Verified,Stale,Unverified}
    public sealed class LiveClashDto : ClashResolveAI.Core.ClashResult {public long SessionGeneration;public LiveVerificationState Verification;}
    public static class LiveClashResolver {public static bool IsCurrent(Autodesk.Revit.DB.Document doc,LiveClashDto row)=>!row.Stale;
        public static ClashResolveAI.Core.ClashResult Resolve(Autodesk.Revit.DB.Document doc,LiveClashDto row)=>row;}
    public sealed class LiveMonitorService {
        public bool HasPendingWork;
        public readonly List<string> Calls=new();
        public void SessionRenewed(string key,long gen){Calls.Add("renew:"+key);}
        public void DocumentClosed(string key){Calls.Add("close:"+key);}
        public void ClearSessionCore(Autodesk.Revit.DB.Document doc){Calls.Add("clear:"+doc.Key);}
        public void CancelRecheckCore(Autodesk.Revit.DB.Document doc){Calls.Add("cancel:"+doc.Key);}
        public void RecheckLedgerCore(Autodesk.Revit.DB.Document doc){Calls.Add("refresh:"+doc.Key);}
        public void SetModeCore(string key,MonitorMode mode){Calls.Add("mode:"+key+":"+mode);}
        public void CheckSelectionCore(){Calls.Add("selection");}
        public void AdvanceCore(string key,long generation){Calls.Add("advance:"+key);}
    }
    public sealed class ClashSnapshotResult {public string DocumentKey="";public long SessionGeneration;}
    public sealed class ClashSnapshotStore {public static ClashSnapshotStore Instance=new();public ClashSnapshotResult? Latest;public void Publish(ClashSnapshotResult r){Latest=r;} }
    public sealed class ClashSnapshotHandler {public void Capture(Autodesk.Revit.UI.UIApplication app,ClashResolveAI.Core.ClashResult clash,Action<ClashSnapshotResult> completed){completed(new());} }
    public sealed class RadarDataStore {public static RadarDataStore Instance=new();public List<LiveClashDto> Rows=new();public List<LiveClashDto> GetActive()=>Rows;public void MarkStale(string key,ISet<long>? ids,string reason){}}
    public sealed class ClashRadarPanel {
        public static bool IsVisible=true;
        public static ClashRadarPanel Instance=new();
        public string Status="";public int Inspections;
        public void ShowInApi(Autodesk.Revit.UI.UIApplication app,bool show){}
        public void SetScanStatus(string message,bool complete){Status=message;}
        public void AcceptInspection(ClashResolveAI.Core.ClashResult clash,ClashResolveAI.Inspection.InspectionScene? scene,string error){Inspections++;}
    }
}
