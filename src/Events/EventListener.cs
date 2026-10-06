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
    // Event adapter only: records committed IDs (including Undo/Redo).
    // No ScanJob ownership, debounce, geometry, instance expansion or reconciliation.
    public sealed class EventListener
    {
        private readonly UIApplication _app;
        private readonly LiveScanScheduler _scheduler;
        public bool IsActive {get;private set;}
        internal ClashResolveAI.ClashEngine.ScanStatistics? LastStatistics=>_scheduler.LastStatistics;
        internal double MaximumApiSliceMilliseconds=>_scheduler.MaximumApiSliceMilliseconds;
        internal LiveScanStatus Status=>_scheduler.StatusFor(DocumentSession.CurrentKey);
        internal LiveScanStatus StatusFor(string key)=>_scheduler.StatusFor(key);
        internal void SetMode(string key,MonitorMode mode)=>_scheduler.SetMode(key,mode);
        public bool HasPendingWork=>IsActive&&_scheduler.NeedsPump(DocumentSession.CurrentKey);
        public EventListener(UIApplication app,AlertSystem alert){_app=app;_scheduler=new LiveScanScheduler(app,alert);}
        public void Start(){if(IsActive)return;IsActive=true;_app.Application.DocumentChanged+=OnChanged;_app.Application.DocumentClosing+=OnClosing;_app.SelectionChanged+=OnSelection;RadarDataStore.Instance.BeginSession();}
        public void Stop(){if(!IsActive)return;IsActive=false;_app.Application.DocumentChanged-=OnChanged;_app.Application.DocumentClosing-=OnClosing;_app.SelectionChanged-=OnSelection;_scheduler.Dispose();LiveSessionLedger.Store.Clear();}
        public void ClearReported(){}
        public bool CheckCurrentSelection(){
            if(_scheduler.Mode!=MonitorMode.Live)return false;
            var ui=_app.ActiveUIDocument;if(ui==null||ui.Document.IsFamilyDocument)return false;
            var ids=ui.Selection.GetElementIds();_scheduler.Record(ui.Document,ids,LiveChangeKind.Check);return ids.Count>0;
        }
        private void OnSelection(object sender,SelectionChangedEventArgs args){try{CheckCurrentSelection();}catch(Exception ex){Diagnostics.Log("Selection change adapter",ex);}}
        public void RecheckLedger(Document doc,List<ElementId> ids){_scheduler.RequestCheck(doc,ids);LiveSessionLedger.Store.Pending(DocumentSession.Key(doc),new HashSet<long>(ids.Select(id=>id.Value)));}
        public void CancelChecks(Document doc)=>_scheduler.Cancel(DocumentSession.Key(doc));
        public void ClearChecks(Document doc)=>_scheduler.Clear(DocumentSession.Key(doc));
        internal void SessionRenewed(string key,long generation)=>_scheduler.SessionRenewed(key,generation);
        internal void DocumentClosed(string key){_scheduler.Close(key);LiveSessionLedger.Store.Clear(key);}
        internal void Advance(string key,long generation)=>_scheduler.Advance(key,generation);
        internal void FullScanStarted(string key,long revision)=>_scheduler.FullScanStarted(key,revision);
        internal void FullScanEnded(Document? doc,string key,bool success,bool complete,ScanScope? scope,long revision)=>_scheduler.FullScanEnded(doc,key,success,complete,scope,revision);
        private void OnChanged(object sender,DocumentChangedEventArgs args){
            try {
                if(_scheduler.Mode==MonitorMode.Off)return;
                var doc=args.GetDocument();if(doc.IsFamilyDocument||doc.IsLinked||ModelChangePolicy.IsViewOnly(args.GetTransactionNames()))return;
                string key=DocumentSession.Key(doc);if(LiveMonitorService.Instance.SessionGeneration(key)==0)return;
                var added=args.GetAddedElementIds();var modified=args.GetModifiedElementIds();var deleted=args.GetDeletedElementIds();
                var changedUtc=DateTime.UtcNow;
                bool global=added.Concat(modified).Concat(deleted).Any(id=>ScanCoordinator.IsInput(key,id.Value));
                _scheduler.Record(doc,added,LiveChangeKind.Added,global,changedUtc);
                _scheduler.Record(doc,modified,LiveChangeKind.Modified,global,changedUtc);
                _scheduler.Record(doc,deleted,LiveChangeKind.Deleted,global,changedUtc);
            }catch(Exception ex){Diagnostics.Log("Document change adapter",ex);}
        }
        private void OnClosing(object sender,DocumentClosingEventArgs args){
            // A closing event may be cancelled. Dispose work but preserve changes.
            _scheduler.SessionRenewed(DocumentSession.Key(args.Document),LiveMonitorService.Instance.SessionGeneration(DocumentSession.Key(args.Document)));
        }
    }
}
