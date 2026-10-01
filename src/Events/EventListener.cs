using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ClashResolveAI.Alert;
using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Events
{
    // Event handlers enqueue document-scoped IDs only. Geometry runs in Revit API callbacks.
    public sealed class EventListener
    {
        private sealed class Pending
        {
            public HashSet<long> Ids = new HashSet<long>();
            public DateTime Changed;
            public long Version;
            public int Failures;
            public HashSet<long>? Recheck;
        }
        private sealed class WakeHandler : IExternalEventHandler
        {
            private readonly EventListener _owner;
            public WakeHandler(EventListener owner) { _owner=owner; }
            public void Execute(UIApplication app) { if(_owner.IsActive)_owner.ProcessWork(null); }
            public string GetName() => "Clash Radar queued geometry";
        }
        private ExternalEvent? _wakeEvent;
        private System.Windows.Threading.DispatcherTimer? _wakeTimer;
        private ClashResolveAI.ClashEngine.ScanJob? _job;
        private string _jobKey="";
        private long _jobVersion;
        private HashSet<long> _batch=new HashSet<long>();
        internal ClashResolveAI.ClashEngine.ScanStatistics? LastStatistics {get;private set;}
        private DateTime _next;
        private DateTime _nextStatus;
        public bool HasPendingWork => IsActive && _pending.Values.Any(p=>p.Failures<3&&p.Ids.Count>0);
        private void Status(string message,bool checking=false)
        { if(ClashRadarPanel.IsVisible)ClashRadarPanel.Instance.SetLiveStatus(message,checking); }
        private readonly UIApplication _app;
        private readonly AlertSystem _alert;
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();
        public bool IsActive { get; private set; }
        public EventListener(UIApplication app, AlertSystem alert) { _app = app; _alert = alert; }
        public void Start()
        {
            if (IsActive) return;
            _app.Application.DocumentChanged += OnChanged;
            _app.Application.DocumentClosing += OnClosing;
            _app.SelectionChanged += OnSelection;
            _app.Idling += OnIdling;
            _wakeEvent=ExternalEvent.Create(new WakeHandler(this));
            _wakeTimer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(50) };
            _wakeTimer.Tick += Wake;
            IsActive = true;
            _wakeTimer.Start();
            RadarDataStore.Instance.BeginSession();
        }
        public void Stop()
        {
            if (!IsActive) return;
            IsActive=false;
            _wakeTimer?.Stop();
            if(_wakeTimer!=null)_wakeTimer.Tick-=Wake;
            _wakeTimer=null;
            _wakeEvent?.Dispose();_wakeEvent=null;
            _app.Application.DocumentChanged -= OnChanged;
            _app.Application.DocumentClosing -= OnClosing;
            _app.SelectionChanged -= OnSelection;
            _app.Idling -= OnIdling;
            _job?.Dispose(); _job=null; _pending.Clear(); IsActive = false;
            LiveSessionLedger.Store.Clear();
            if(ClashRadarPanel.IsVisible)ClashRadarPanel.Instance.SetScanStatus("Live monitoring stopped",true);
        }
        private void Wake(object? sender, EventArgs args)
        {
            // No model API access from the timer. ExternalEvent explicitly wakes
            // Revit when no Idling session is running after a transaction.
            if(!IsActive || DateTime.UtcNow<_next)return;
            if(_pending.Values.Any(p=>p.Failures<3&&p.Ids.Count>0))
                _wakeEvent?.Raise();
        }
        public void ClearReported() { }
        public bool CheckCurrentSelection()
        {
            var ui = _app.ActiveUIDocument;
            if (ui == null || ui.Document.IsFamilyDocument) return false;
            var ids = ui.Selection.GetElementIds();
            Queue(ui.Document, ids, false);
            return ids.Count > 0;
        }
        private void OnSelection(object sender, SelectionChangedEventArgs args)
        {
            try { CheckCurrentSelection(); } catch (Exception ex) { Diagnostics.Log("Selection queue", ex); }
        }
        public void Queue(Document doc, IEnumerable<ElementId> ids, bool modelChanged=true)
        {
            if (!_pending.TryGetValue(DocumentSession.Key(doc), out var work)) _pending[DocumentSession.Key(doc)] = work = new Pending();
            var batch=ids.ToList();
            if(batch.Count==0)return;
            bool added=false;
            foreach (var id in batch) added|=work.Ids.Add(id.Value);
            if(!added&&!modelChanged)return;
            // Selection does not invalidate geometry already being checked. Keep
            // newly selected IDs queued for the next batch instead of losing them.
            if(modelChanged)work.Version++;
            if(_job==null||modelChanged)work.Changed = DateTime.UtcNow;
            work.Failures=0;
            Status($"Queued {work.Ids.Count} elements for local checks",true);
        }
        public void RecheckLedger(Document doc,List<ElementId> ids)
        {
            Queue(doc,ids,modelChanged:true);
            var work=_pending[DocumentSession.Key(doc)];work.Recheck=new HashSet<long>(ids.Select(id=>id.Value));
            LiveSessionLedger.Store.Pending(DocumentSession.Key(doc),work.Recheck);
        }
        public void CancelChecks(Document doc)
        {
            string key=DocumentSession.Key(doc);
            if(_jobKey==key){_job?.Dispose();_job=null;}
            _pending.Remove(key);
            if(ClashRadarPanel.IsVisible)ClashRadarPanel.Instance.SetScanStatus("Re-check cancelled; unchecked elements remain in the session",true);
        }
        private void OnChanged(object sender, DocumentChangedEventArgs args)
        {
            try
            {
                var doc = args.GetDocument();
                if (doc.IsFamilyDocument || doc.IsLinked) return;
                // View-only transactions must not recursively trigger geometry scans.
                if (ModelChangePolicy.IsViewOnly(args.GetTransactionNames())) return;
                var scope=ScanSessionCache.Filter(doc,AppSettings.Load());
                var added=args.GetAddedElementIds(scope).Where(id=>!(doc.GetElement(id) is ElementType)).ToList();
                var edited=args.GetModifiedElementIds(scope).Where(id=>!(doc.GetElement(id) is ElementType)).ToList();
                LiveSessionLedger.Note(doc,added,LedgerOrigin.Drawn);
                var tracked=new HashSet<long>(LiveSessionLedger.Store.Entries(DocumentSession.Key(doc)).Select(e=>e.ElementId));
                LiveSessionLedger.Note(doc,edited.Where(id=>AppSettings.Load().TrackEditedElements||tracked.Contains(id.Value)),LedgerOrigin.Edited);
                LiveSessionLedger.LiveIds(doc); // Undo/deletion and replaced numeric IDs are pruned by UniqueId.
                string key=DocumentSession.Key(doc);
                var changed=args.GetAddedElementIds().Concat(args.GetModifiedElementIds()).ToList();
                var types=new HashSet<long>(changed.Where(id=>doc.GetElement(id) is ElementType).Select(id=>id.Value));
                bool inputs=changed.Any(id=>ScanCoordinator.IsInput(key,id.Value))||args.GetDeletedElementIds().Any(id=>ScanCoordinator.IsInput(key,id.Value));
                // Input edits invalidate any in-flight work, but never enqueue a global job.
                if(inputs){if(_jobKey==key){_job?.Dispose();_job=null;}_pending.Remove(key);}
                var expanded=types.Count==0?new List<ElementId>():new FilteredElementCollector(doc).WherePasses(scope)
                    .WhereElementIsNotElementType().Where(e=>types.Contains(e.GetTypeId().Value)).Take(501).Select(e=>e.Id).ToList();
                bool overCap=expanded.Count>500;
                // Revit may report all dependent instances Modified for an input edit.
                // Only the bounded type expansion is eligible in that transaction;
                // level/link changes and oversized type edits remain banner-only.
                var ids=inputs?(overCap?new List<ElementId>():expanded):added.Concat(edited)
                    .Concat(args.GetDeletedElementIds().Where(id=>!ScanCoordinator.IsInput(key,id.Value))).ToList();
                Queue(doc,ids);
                if(inputs)Status(overCap?"Type affects more than 500 instances; run Full Scan":"Model inputs changed; run Full Scan to re-verify");
                if(ClashRadarPanel.IsVisible)ClashRadarPanel.Instance.UpdateLedgerCount();
            }
            catch (Exception ex) { Diagnostics.Log("Document change queue", ex); }
        }
        private void OnClosing(object sender, DocumentClosingEventArgs args)
        {
            if(_jobKey==DocumentSession.Key(args.Document)){_job?.Dispose();_job=null;}
            _pending.Remove(DocumentSession.Key(args.Document));
            LiveSessionLedger.Clear(args.Document);
            if(ClashRadarPanel.IsVisible)ClashRadarPanel.Instance.SetScanStatus("Project closed",true);
        }
        private void OnIdling(object sender, IdlingEventArgs args) => ProcessWork(args);
        private void ProcessWork(IdlingEventArgs? args)
        {
            try
            {
                var doc = _app.ActiveUIDocument?.Document;
                if (doc == null || doc.IsFamilyDocument || doc.IsReadOnly) return;
                DocumentSession.Activate(doc);
                string key=DocumentSession.Key(doc);
                if (_job!=null&&(_jobKey!=key||!_pending.TryGetValue(key,out var active)||active.Version!=_jobVersion))
                { _job.Dispose(); _job=null;if(ClashRadarPanel.IsVisible)ClashRadarPanel.Instance.SetScanStatus("Re-check queued for the current project",true); }
                // Full scans and local checks own separate caches and run serially
                // on Revit's API thread; a long full scan must not block live work.
                if (!_pending.TryGetValue(key, out var work) || work.Ids.Count == 0) return;
                if(work.Failures>=3)return;
                // Keep requesting idle callbacks during the throttle window.
                args?.SetRaiseWithoutDelay();
                if(DateTime.UtcNow<_next)return;
                if ((DateTime.UtcNow - work.Changed).TotalMilliseconds < AppSettings.Load().LiveDebounceMilliseconds) return;
                if(_job==null) {
                    _batch=new HashSet<long>(work.Recheck??work.Ids);
                    _jobKey=key;_jobVersion=work.Version;
                    _job=new ClashResolveAI.ClashEngine.ClashEngine(doc,AppSettings.Load().RuleSetName)
                        .CreateJob(_batch.Select(id=>new ElementId(id)),AppSettings.Load().ScanLinkedModels);
                }
                bool complete=_job.Advance(Math.Max(5,Math.Min(40,AppSettings.Load().LiveSliceMilliseconds)));
                _next=DateTime.UtcNow.AddMilliseconds(25);
                if(!complete){
                    if(DateTime.UtcNow>=_nextStatus){Status($"Checking {_batch.Count} elements · {_job.Statistics.Tested} pairs tested",true);_nextStatus=DateTime.UtcNow.AddMilliseconds(250);}
                    return;
                }
                var found=_job.Results;
                int checkedSources=_job.Statistics.Sources;
                LastStatistics=_job.Statistics;
                var scanMode=_job.Statistics.Mode;var scanScope=_job.Statistics.Scope;
                foreach(var c in found){c.GeometryRevision=ScanCoordinator.Revision;c.Origin=ResultOrigin.Live;c.LiveSessionId=RadarDataStore.Instance.SessionId;}
                Diagnostics.Log("Live scan: "+_job.Statistics.Summary+"; latency "+(DateTime.UtcNow-work.Changed).TotalMilliseconds.ToString("F0")+" ms");
                _job.Dispose();_job=null;

                var old=RadarDataStore.Instance.GetActive().Select(c=>c.NormalizedKey).ToHashSet();
                RadarDataStore.Instance.Reconcile(_batch,found,scanMode,scanScope);
                work.Ids.ExceptWith(_batch);work.Recheck=null;
                ScanCoordinator.MarkChecked(key,_batch);
                if(ClashRadarPanel.IsVisible)ClashRadarPanel.Instance.SetScanStatus($"Checked {checkedSources} session elements · {found.Count} results",true);
                Status($"Checked {checkedSources} elements · {found.Count(c=>c.TestType!=ClashTestType.Unverified)} issues · {found.Count(c=>c.TestType==ClashTestType.Unverified)} need review");
                var fresh=found.Where(c=>c.TestType!=ClashTestType.Unverified&&!old.Contains(c.NormalizedKey)).ToList();
                if(fresh.Count>0&&AppSettings.Load().ShowToast)_alert.TriggerAlerts(_app,fresh);

            }
            catch (Exception ex) {
                _job?.Dispose();_job=null;_next=DateTime.UtcNow.AddSeconds(5);
                if(_pending.TryGetValue(_jobKey,out var failed))failed.Failures++;
                Status("Live check failed: "+ex.Message);
                if(ClashRadarPanel.IsVisible)ClashRadarPanel.Instance.SetScanStatus("Live check failed: "+ex.Message,true);
                Diagnostics.Log("Live scan failed; retry backoff, at most 3 attempts until next edit",ex);
            }
        }
    }
}
