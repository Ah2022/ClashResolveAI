// Deterministic model stand-ins. These tests exercise production scheduler,
// resolver and DTO/store code, not native Revit geometry or event timing.
using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;
namespace Autodesk.Revit.DB
{
    public sealed class ElementId {public long Value {get;}public ElementId(long value){Value=value;}}
    public sealed class Category {public string Name {get;set;}="Test";}
    public class Element {
        public bool IsValidObject {get;set;}=true;
        public ElementId Id {get;set;}=new(1);
        public string UniqueId {get;set;}="uid:1";
        public Guid VersionGuid {get;set;}=Guid.NewGuid();
        public Category Category {get;set;}=new();
    }
    public sealed class ElementType : Element {}
    public sealed class XYZ {
        public double X {get;}public double Y {get;}public double Z {get;}
        public XYZ(double x,double y,double z){X=x;Y=y;Z=z;}
        public static XYZ Zero=>new(0,0,0);
    }
    public sealed class Transform {public XYZ Origin=XYZ.Zero,BasisX=new(1,0,0),BasisY=new(0,1,0),BasisZ=new(0,0,1);}
    public sealed class RevitLinkInstance : Element {
        public Document? Linked;
        public Transform Transform=new();
        public Document? GetLinkDocument()=>Linked;
        public Transform GetTotalTransform()=>Transform;
    }
    public sealed class Document {
        public string Key="host";
        public bool IsValidObject=true,IsReadOnly;
        public Guid Version=Guid.NewGuid();
        public Dictionary<long,Element> Elements=new();
        public Element? GetElement(ElementId id)=>Elements.TryGetValue(id.Value,out var value)&&value.IsValidObject?value:null;
        public Element? GetElement(string uid)=>Elements.Values.FirstOrDefault(e=>e.IsValidObject&&e.UniqueId==uid);
        public static DocumentVersion GetDocumentVersion(Document doc)=>new(){VersionGUID=doc.Version};
    }
    public sealed class DocumentVersion : IDisposable {public Guid VersionGUID;public int NumberOfSaves;public void Dispose(){} }
    public sealed class Solid {}
    public sealed class BoundingBoxXYZ {}
    public sealed class ElementMulticategoryFilter {public bool PassesFilter(Element e)=>!(e is ElementType)&&!(e is RevitLinkInstance);}
    public sealed class FilteredElementCollector : IEnumerable<Element> {
        private readonly Document _doc;private Type? _type;
        public FilteredElementCollector(Document doc){_doc=doc;}
        public FilteredElementCollector OfClass(Type type){_type=type;return this;}
        public IEnumerator<Element> GetEnumerator()=>_doc.Elements.Values.Where(e=>e.IsValidObject&&(_type==null||_type.IsInstanceOfType(e))).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
}
namespace Autodesk.Revit.UI
{
    public sealed class UIDocument {public Document Document=new();}
    public sealed class UIApplication {public UIDocument? ActiveUIDocument=new();}
}
namespace ClashResolveAI
{
    public sealed class AppSettings {
        public static AppSettings Current=new();
        public string RuleSetName="DefaultRules";
        public ScanMode LiveMode=ScanMode.HardOnly,FullScanMode=ScanMode.HardOnly;
        public bool IncludeLinkToLink,ScanWithinLinks=true,IncludeStructural=true,ScanLinkedModels=true,IncludeGenericModels=true,IncludeInsulation=true,ExcludeConnectedJoints=true,ExcludeNamedSupports,TrackEditedElements=true,ShowToast;
        public double MinimumOverlapMM3=1,InsulationMM=50,MaintenanceMM=100;
        public int LiveDebounceMilliseconds=300,LiveSliceMilliseconds=5,LiveMaximumSliceMilliseconds=100;
        public static AppSettings Load()=>Current;
        public AppSettings ScanSnapshot()=>(AppSettings)MemberwiseClone();
    }
}
namespace ClashResolveAI.Rules {public static class RulesEngine {public static string Contents="rules";public static int CaptureDelay;public static string CacheKey(string name){if(CaptureDelay>0)Thread.Sleep(CaptureDelay);return name+Contents;}}}
namespace ClashResolveAI.Core
{
    public static class DocumentSession {public static string CurrentKey="host";public static string Key(Document doc)=>doc.Key;public static bool Matches(ClashResult row,Document doc)=>row.HostDocumentKey==doc.Key;}
    public static class Diagnostics {public static void Log(string message,Exception? ex=null){} }
    public static class ScanSessionCache {public static ElementMulticategoryFilter Filter(Document doc,AppSettings s)=>new();}
    public static class ScanCoordinator {
        public static ElementRevisionStore Revisions=new();
        public static bool InputsStale=>Revisions.InputsStale(DocumentSession.CurrentKey);
        public static long DocumentRevision(string key)=>Revisions.DocumentRevision(key);
        public static long InputRevision(string key)=>Revisions.InputRevision(key);
        public static bool IsStale(LiveClashDto row)=>Revisions.Required(row.HostDocumentKey,row.ElementAId,row.LinkInstanceA,row.ElementBId,row.LinkInstanceB)>row.GeometryRevision;
        public static void MarkChecked(string key,ISet<long> ids){Revisions.Checked(key,ids);LiveSessionLedger.Store.Checked(key,ids);}
    }
}
namespace ClashResolveAI.Dashboard {public sealed class ClashDashboard {public static ClashDashboard Instance=new();public void UpdateClashStatus(string id,ClashStatus status){} }}
namespace ClashResolveAI.Alert {public sealed class AlertSystem {public int Alerts;public void TriggerLiveAlerts(IReadOnlyList<LiveClashDto> rows){Alerts+=rows.Count;}}}
namespace ClashResolveAI.LiveMonitor
{
    public sealed class LiveMonitorService {
        public static LiveMonitorService Instance=new();public long Generation=1;
        public Action<string,long>? OnRenew;
        public long SessionGeneration(string key)=>Generation;
        public void PublishStatus(string key,LiveScanStatus status){}
        public bool IsCurrent(string key,long generation)=>key==DocumentSession.CurrentKey&&generation==Generation;
        public void RenewAfterFullScan(string key){Generation++;OnRenew?.Invoke(key,Generation);}
    }
    public sealed class ClashRadarPanel {public static bool IsVisible=true;public static ClashRadarPanel Instance=new();public void SetLiveStatus(string message,bool checking){}public void SetScanStatus(string message,bool done){}public void UpdateLedgerCount(){} }
    public static class LiveSessionLedger {
        public static LiveLedgerStore Store=new();
        public static void Note(Document doc,IEnumerable<ElementId> ids,LedgerOrigin origin){foreach(var id in ids){var e=doc.GetElement(id);if(e!=null)Store.Note(doc.Key,id.Value,e.UniqueId,origin);}}
        public static List<ElementId> LiveIds(Document doc)=>Store.LiveIds(doc.Key,id=>doc.GetElement(new ElementId(id))?.UniqueId).Select(id=>new ElementId(id)).ToList();
    }
}
namespace ClashResolveAI.ClashEngine
{
    public sealed class ClashEngine {
        public static int Jobs,Disposed;
        public static bool Infinite,Unreliable,Emit=true;
        public static int StepDelay;
        private readonly Document _doc;
        public ClashEngine(Document doc,string rules){_doc=doc;}
        public ScanJob CreateJob(IEnumerable<ElementId> ids,bool links,AppSettings? settingsSnapshot=null){
            Jobs++;var sources=ids.ToList();var rows=new List<ClashResult>();var stats=new ScanStatistics{Scope=new ScanScope(_doc.Key),Mode=ScanMode.HardOnly};
            IEnumerable<int> Work(){
                if(StepDelay>0)Thread.Sleep(StepDelay);
                while(Infinite)yield return 0;
                foreach(var id in sources){
                    stats.Sources++;stats.Scope.Note(id.Value,"","");
                    if(Unreliable){stats.MissingGeometry++;stats.Scope.MissingSource(id.Value,"");continue;}
                    if(Emit&&id.Value==1&&_doc.GetElement(new ElementId(1)) is Element a&&_doc.GetElement(new ElementId(2)) is Element b)
                        rows.Add(new ClashResult{ElementA=a,ElementB=b,HostDocumentKey=_doc.Key,TestType=ClashTestType.HardClash,Status=ClashStatus.Active});
                    yield return 0;
                }
            }
            return new ScanJob(Work(),rows,stats,()=>Disposed++);
        }
    }
}
