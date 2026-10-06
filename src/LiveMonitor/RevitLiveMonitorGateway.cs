using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using ClashResolveAI.Inspection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;

namespace ClashResolveAI.LiveMonitor
{
    // The only ExternalEvent owner in the live subsystem. Existing scan engine
    // and API helpers remain unchanged; all live dispatch is validated here.
    internal sealed class RevitLiveMonitorGateway : IExternalEventHandler, IDisposable
    {
        internal sealed class PinPayload
        {
            public InspectionScene Scene { get; }
            public InspectorPreferences Preferences { get; }
            public PinPayload(InspectionScene scene, InspectorPreferences p) {
                // Pinning needs framing/axes only. Freeze these values instead
                // of retaining a scene whose mesh/context collections the UI owns.
                Scene=new InspectionScene {Project=scene.Project, IssueId=scene.IssueId,
                    Min=scene.Min, Max=scene.Max, Clash=scene.Clash, AxisA=scene.AxisA,
                    AxisB=scene.AxisB, Notice=scene.Notice, OverlapVolume=scene.OverlapVolume};
                Preferences=new InspectorPreferences {PaddingFeet=p.PaddingFeet, Focus=p.Focus,
                    Context=p.Context, Corner=p.Corner, AxisB=p.AxisB};
            }
        }
        private readonly LiveSessionRegistry _sessions = new LiveSessionRegistry();
        private readonly LiveRequestQueue _queue = new LiveRequestQueue();
        private readonly LiveMonitorService _owner;
        private readonly ExternalEvent _event;
        private readonly DispatcherTimer _timer;
        private readonly ClashSnapshotHandler _snapshots = new ClashSnapshotHandler();
        private bool _outstanding, _disposed;
        public long StaleRequestCount { get; private set; }
        public int QueueDepth => _queue.Count;
        public long Generation(string key) => _sessions.Generation(key);
        public bool IsCurrent(string key,long generation) => !_disposed && _sessions.IsCurrent(key,generation);
        public RevitLiveMonitorGateway(LiveMonitorService owner) {
            _owner=owner; _event=ExternalEvent.Create(this);
            _timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)};
            _timer.Tick+=Tick; _timer.Start();
        }
        public void Activate(string key) {
            if(_disposed)return;
            string previous=_sessions.Activate(key);
            if(previous!="")_owner.SessionRenewed(previous,Generation(previous));
        }
        public void Renew(string key) {
            _sessions.Renew(key);
            _owner.SessionRenewed(key,Generation(key));
        }
        public void Close(string key) {
            _queue.RemoveDocument(key); _sessions.Close(key);
            _owner.DocumentClosed(key);
        }
        public bool Submit(LiveOperation operation,string clashKey="",long revision=0,object? payload=null,string? documentKey=null,long? sessionGeneration=null) {
            string key=documentKey??_sessions.ActiveKey;
            long generation=sessionGeneration??Generation(key);
            if(!IsCurrent(key,generation)) { Reject("Live request rejected: project or session changed; re-check the session",new LiveRequest(key,generation,operation));return false; }
            var request=new LiveRequest(key,generation,operation,clashKey,revision,payload);
            if(!_queue.Enqueue(request)) { Report("Live request queue is full; try again after the current operation");return false; }
            LiveDiagnostics.Request(key,generation,request.RequestId,operation.ToString(),_queue.Count,_queue.Coalesced);
            Raise(); return true;
        }
        private void Tick(object? sender,EventArgs args) {
            if(_disposed)return;
            if(_queue.Count==0 && _owner.HasPendingWork) Submit(LiveOperation.Advance);
            else if(_queue.Count>0) Raise();
        }
        private void Raise() {
            if(_disposed||_outstanding||_queue.Count==0)return;
            try {
                var result=_event.Raise();
                _outstanding=result==ExternalEventRequest.Accepted || result==ExternalEventRequest.Pending;
                if(!_outstanding)LiveDiagnostics.Retry(_sessions.ActiveKey);
                if(!_outstanding)Diagnostics.Log("Live gateway wake deferred: "+result);
            }catch(Exception ex){Diagnostics.Log("Live gateway wake",ex);}
        }
        private bool Valid(UIApplication app,LiveRequest request) {
            var doc=app.ActiveUIDocument?.Document;
            return doc!=null && doc.IsValidObject && !doc.IsFamilyDocument && !doc.IsLinked &&
                IsCurrent(request.DocumentKey,request.SessionGeneration) && DocumentSession.Key(doc)==request.DocumentKey;
        }
        public void Execute(UIApplication app) {
            _outstanding=false;
            if(_disposed)return;
            // Bounded drain. One geometry slice or expensive user action per callback.
            var budget=Stopwatch.StartNew();
            for(int count=0;count<16 && budget.ElapsedMilliseconds<25;count++) {
                var request=_queue.Dequeue();if(request==null)break;
                if(!Valid(app,request)){Reject("Live request rejected: project or session changed",request);continue;}
                try {
                    var doc=app.ActiveUIDocument!.Document;
                    switch(request.Operation) {
                        case LiveOperation.Mode: _owner.SetModeCore(request.DocumentKey,(MonitorMode)request.Payload!);break;
                        case LiveOperation.Pane: ClashRadarPanel.Instance.ShowInApi(app,(bool)request.Payload!);break;
                        case LiveOperation.Clear: Renew(request.DocumentKey);_owner.ClearSessionCore(doc);break;
                        case LiveOperation.Cancel: _owner.CancelRecheckCore(doc);break;
                        case LiveOperation.Refresh: _owner.RecheckLedgerCore(doc);break;
                        case LiveOperation.Selection: _owner.CheckSelectionCore();break;
                        case LiveOperation.Advance: _owner.AdvanceCore(request.DocumentKey,request.SessionGeneration);break;
                        default:
                            var dto=RadarDataStore.Instance.GetActive().FirstOrDefault(c=>c.HostDocumentKey==request.DocumentKey && c.NormalizedKey==request.ClashKey);
                            if(dto==null || dto.SessionGeneration!=request.SessionGeneration || dto.GeometryRevision!=request.GeometryRevision || ScanCoordinator.IsStale(dto) || dto.Verification!=LiveVerificationState.Verified) {
                                Reject("Elements changed or the clash is no longer active; re-check before using this result",request);break;
                            }
                            ClashResult clash;
                            try{clash=LiveClashResolver.Resolve(doc,dto);}
                            catch(InvalidOperationException ex){
                                Reject(ex.Message,request);
                                RadarDataStore.Instance.MarkStale(request.DocumentKey,new HashSet<long>{dto.ElementAId,dto.ElementBId},ex.Message);
                                break;
                            }
                            if(request.Operation==LiveOperation.Navigate2D || request.Operation==LiveOperation.Navigate3D)
                                Services.ClashViewNavigation.Show(app,clash,request.Operation==LiveOperation.Navigate3D,()=>Valid(app,request) && LiveClashResolver.IsCurrent(app.ActiveUIDocument!.Document,dto));
                            else if(request.Operation==LiveOperation.Snapshot)
                                _snapshots.Capture(app,clash,r=> {if(Valid(app,request)&&LiveClashResolver.IsCurrent(doc,dto)) {r.DocumentKey=request.DocumentKey;r.SessionGeneration=request.SessionGeneration;ClashSnapshotStore.Instance.Publish(r);}});
                            else {
                                var pin=request.Payload as PinPayload;
                                InspectionHandler.Run(app,clash,pin?.Scene,pin?.Preferences,
                                    (scene,error)=> {if(Valid(app,request) && LiveClashResolver.IsCurrent(doc,dto) && ClashRadarPanel.IsVisible)ClashRadarPanel.Instance.AcceptInspection(dto,scene,error);});
                            }
                            break;
                    }
                    if(request.Operation!=LiveOperation.Advance)Diagnostics.Log($"Live request {request.RequestId}: {request.Operation}; document {request.DocumentKey}; generation {request.SessionGeneration}; queue {_queue.Count}; delay {(DateTime.UtcNow-request.CreatedUtc).TotalMilliseconds:F0} ms");
                }catch(Exception ex){Diagnostics.Log("Live gateway execution",ex);Report("Live operation failed: "+ex.Message);}
                if(request.Operation==LiveOperation.Advance || request.Operation==LiveOperation.Inspect || request.Operation==LiveOperation.Pin || request.Operation==LiveOperation.Snapshot)break;
            }
            // Timer retries unaccepted wakes and continues a bounded drain.
        }
        private void Reject(string message,LiveRequest? request=null) {StaleRequestCount++;LiveDiagnostics.Request(request?.DocumentKey??_sessions.ActiveKey,request?.SessionGeneration??Generation(_sessions.ActiveKey),request?.RequestId??Guid.NewGuid(),"Rejected",_queue.Count,_queue.Coalesced,true);Diagnostics.Log(message+"; stale request count "+StaleRequestCount);Report(message);}
        private static void Report(string message) {if(ClashRadarPanel.IsVisible)ClashRadarPanel.Instance.SetScanStatus(message,true);}
        public string GetName()=>"ClashResolve document-scoped live gateway";
        public void Dispose() {if(_disposed)return;_disposed=true;_timer.Stop();_timer.Tick-=Tick;_queue.Clear();_sessions.Clear();_event.Dispose();}
    }
}
