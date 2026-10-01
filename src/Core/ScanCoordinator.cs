using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ClashResolveAI.ClashEngine;
using ClashResolveAI.Commands;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Core
{
    internal static class ScanCoordinator
    {
        private static ScanJob? _job;
        private static string _documentKey="";
        private static Action<List<ClashResult>,ScanStatistics>? _done;
        private static Action<string>? _progress;
        private static Action<string>? _failed;
        private sealed class Pump : IExternalEventHandler
        {
            public void Execute(UIApplication app) => Advance(app);
            public string GetName() => "ClashResolve full scan pipeline";
        }
        private static ExternalEvent? _event;
        private static System.Windows.Threading.DispatcherTimer? _timer;
        private static DateTime _nextProgress;
        private static readonly ElementRevisionStore Revisions=new ElementRevisionStore();
        private static readonly Dictionary<string,HashSet<long>> Inputs=new Dictionary<string,HashSet<long>>();
        private static bool _completeScope;
        public static void Observe(Document doc)
        {
            string key=DocumentSession.Key(doc);if(Inputs.ContainsKey(key))return;
            Inputs[key]=new HashSet<long>(new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds().Select(id=>id.Value)
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Level)).ToElementIds().Select(id=>id.Value))
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Family)).ToElementIds().Select(id=>id.Value))
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).ToElementIds().Select(id=>id.Value)));
        }
        public static bool InputsStale => Revisions.InputsStale(DocumentSession.CurrentKey);
        public static bool FullResultsStale => Revisions.FullResultsStale(DocumentSession.CurrentKey);
        public static bool IsInput(string key,long id)=>Inputs.TryGetValue(key,out var ids)&&ids.Contains(id);
        public static bool IsStale(ClashResult clash)=>Revisions.IsStale(clash);
        public static long RequiredRevision(ClashResult clash)=>Revisions.Required(clash);
        public static void MarkChecked(string key,ISet<long> ids){Revisions.Checked(key,ids);LiveMonitor.LiveSessionLedger.Store.Checked(key,ids);}
        public static void Close(string key){Revisions.Close(key);Inputs.Remove(key);LiveMonitor.LiveSessionLedger.Store.Clear(key);}
        public static bool Busy => _job!=null;
        public static ScanStatistics? CurrentStatistics => _job?.Statistics;
        public static long Revision=>Revisions.Revision;
        public static bool ResultsStale=>DocumentSession.CurrentKey!=""&&(Revisions.IsDirty(DocumentSession.CurrentKey)||LiveMonitor.LiveSessionLedger.Store.HasUnchecked(DocumentSession.CurrentKey));
        public static void Attach(UIControlledApplication app)
        {
            _event=ExternalEvent.Create(new Pump());
            _timer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(10) };
            _timer.Tick+=(_,__)=>{if(_job!=null)_event?.Raise();};
            app.ControlledApplication.DocumentChanged+=(_,e)=>{
                var doc=e.GetDocument();if(doc.IsFamilyDocument||doc.IsLinked)return;
                if(ModelChangePolicy.IsViewOnly(e.GetTransactionNames()))return;
                Observe(doc);string key=DocumentSession.Key(doc);
                var all=e.GetAddedElementIds().Concat(e.GetModifiedElementIds()).Concat(e.GetDeletedElementIds()).ToList();
                bool inputs=all.Any(id=>Inputs[key].Contains(id.Value)||doc.GetElement(id) is ElementType||doc.GetElement(id) is Family||doc.GetElement(id) is Level||doc.GetElement(id) is RevitLinkInstance);
                foreach(var id in all)if(doc.GetElement(id) is ElementType||doc.GetElement(id) is Family||doc.GetElement(id) is Level||doc.GetElement(id) is RevitLinkInstance)Inputs[key].Add(id.Value);
                ScanSessionCache.Changed(doc,all);
                var scope=ScanSessionCache.Filter(doc,AppSettings.Load());
                var ids=e.GetAddedElementIds(scope).Concat(e.GetModifiedElementIds(scope)).Where(id=>!(doc.GetElement(id) is ElementType)).Concat(e.GetDeletedElementIds()).Select(id=>id.Value).ToList();
                if(ids.Count==0&&!inputs)return;
                Revisions.Changed(key,ids,inputs);
                if(_job!=null&&key==_documentKey)Cancel("Model changed during scan. Run the scan again.");
                if(LiveMonitor.ClashRadarPanel.IsVisible)LiveMonitor.ClashRadarPanel.Instance.NotifyGeometryChanged();
            };
            app.ControlledApplication.DocumentClosing+=(_,e)=>{if(_job!=null&&DocumentSession.Key(e.Document)==_documentKey)Cancel("Project closed.");ScanSessionCache.Close(e.Document);};
        }
        public static void Start(Document doc, string level,CoordinationZone? zone,Action<List<ClashResult>,ScanStatistics> done,Action<string>? progress=null,Action<string>? failed=null,ScanMode? mode=null,bool? includeLinkToLink=null)
        {
            StartJob(doc,new ClashResolveAI.ClashEngine.ClashEngine(doc,AppSettings.Load().RuleSetName).CreateJob(null,AppSettings.Load().ScanLinkedModels,zone,level,mode,includeLinkToLink),done,progress,failed,string.IsNullOrEmpty(level)&&zone==null);
        }
        internal static void StartJob(Document doc,ScanJob job,Action<List<ClashResult>,ScanStatistics> done,Action<string>? progress=null,Action<string>? failed=null,bool completeScope=false)
        {
            Cancel("Replaced by a newer scan.");
            _documentKey=DocumentSession.Key(doc);_job=job;
            _completeScope=completeScope;
            _done=done;_progress=progress;_failed=failed;_nextProgress=DateTime.MinValue;
            _timer?.Start();_event?.Raise();
        }
        public static void Cancel(string reason="Scan cancelled; previous results retained.")
        {
            _timer?.Stop();
            var job=_job;var failed=_failed;_job=null;_done=null;_progress=null;_failed=null;
            if(job!=null){job.Dispose();failed?.Invoke(reason);}
        }
        public static void Shutdown()
        { Cancel("Revit closing.");_timer?.Stop();_timer=null;_event?.Dispose();_event=null;ScanSessionCache.Clear(); }
        private static void Advance(UIApplication app)
        {
            if(_job==null)return;
            if(app.ActiveUIDocument==null||DocumentSession.Key(app.ActiveUIDocument.Document)!=_documentKey){Cancel("Active project changed.");return;}
            try {
                var job=_job;
                bool done=job.Advance(Math.Max(10,Math.Min(50,LiveMonitor.LiveMonitorService.Instance.HasPendingWork?10:AppSettings.Load().FullScanSliceMilliseconds)));
                if(done||DateTime.UtcNow>=_nextProgress)
                { _progress?.Invoke(job.Statistics.Summary); _nextProgress=DateTime.UtcNow.AddMilliseconds(250); }
                if(!done)return;
                _timer?.Stop();
                var callback=_done;var failure=_failed;_job=null;_done=null;_progress=null;_failed=null;
                foreach(var c in job.Results)c.GeometryRevision=Revision;
                try {
                    callback?.Invoke(job.Results,job.Statistics);
                    var doc=app.ActiveUIDocument.Document;
                    Revisions.CheckedScope(_documentKey,id=>job.Statistics.Scope.CoversHost(id)||(_completeScope&&doc.GetElement(new ElementId(id))==null),_completeScope);
                    if(LiveMonitor.ClashRadarPanel.IsVisible)LiveMonitor.ClashRadarPanel.Instance.NotifyGeometryChanged();
                    LiveMonitor.LiveSessionLedger.Store.Checked(_documentKey,new HashSet<long>(LiveMonitor.LiveSessionLedger.Store.Entries(_documentKey).Where(e=>job.Statistics.Scope.CoversHost(e.ElementId)).Select(e=>e.ElementId)));
                } catch(Exception ex){failure?.Invoke(ex.Message);throw;} finally { job.Dispose(); }
                Diagnostics.Log("Scan complete: "+job.Statistics.Summary);
            }catch(Exception ex){Diagnostics.Log("Scan job failed",ex);Cancel(ex.Message);}
        }
    }
}
