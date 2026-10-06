using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Alert;
using ClashResolveAI.Core;
using ClashResolveAI.Events;
using ClashResolveAI.Inspection;
using System;

namespace ClashResolveAI.LiveMonitor
{
    public sealed class LiveMonitorService
    {
        private static LiveMonitorService? _instance;
        public static LiveMonitorService Instance => _instance ??= new LiveMonitorService();
        private LiveMonitorService(){LiveDiagnostics.Recorded+=Diagnostics.RecordLive;LiveDiagnostics.Recorded+=snapshot=>{if(snapshot.DocumentKey==CurrentDocumentKey)ClashRadarPanel.Instance.ViewModel.SetDiagnostics(snapshot);};}
        private EventListener? _listener;
        private AlertSystem? _alert;
        private RevitLiveMonitorGateway? _gateway;
        public bool IsRunning { get; private set; }
        public MonitorMode Mode {get;private set;}=MonitorMode.Off;
        private void PublishSession(string? document=null){string key=document??CurrentDocumentKey;var viewModel=ClashRadarPanel.Instance.ViewModel;viewModel.SetSession(key,SessionGeneration(key),IsRunning,Mode);LiveDiagnostics.Session(key,SessionGeneration(key),Mode);PublishScope();if(IsRunning&&Mode!=MonitorMode.Off&&_listener!=null)viewModel.SetStatus(_listener.StatusFor(key));}
        private void PublishScope(){var settings=AppSettings.Load();ClashRadarPanel.Instance.ViewModel.SetScope("Affected host elements · "+(settings.ScanLinkedModels?"host + loaded links":"host only")+" · "+(settings.LiveMode==ScanMode.HardOnly?"hard clashes":"hard + clearance"));}
        internal void PublishStatus(string key,LiveScanStatus status){if(key==CurrentDocumentKey&&Mode!=MonitorMode.Off){PublishScope();ClashRadarPanel.Instance.ViewModel.SetStatus(status);}}
        internal bool RequestMode(MonitorMode mode,string key,long generation)=>IsRunning&&_gateway?.Submit(LiveOperation.Mode,payload:mode,documentKey:key,sessionGeneration:generation)==true;
        internal void SetModeCore(string key,MonitorMode mode){if(Mode==mode)return;Mode=mode;_listener?.SetMode(key,mode);if(mode==MonitorMode.Off)_gateway?.Renew(key);PublishSession();if(_listener!=null&&mode!=MonitorMode.Off)PublishStatus(key,_listener.Status);App.MonitorActive=mode!=MonitorMode.Off;App.RefreshMonitorButton();}
        internal bool RequestPane(bool show,string key,long generation)=>IsRunning&&_gateway?.Submit(LiveOperation.Pane,payload:show,documentKey:key,sessionGeneration:generation)==true;
        internal void ShowRadar()=>RequestPane(true,CurrentDocumentKey,SessionGeneration(CurrentDocumentKey));
        internal bool HasPendingWork => IsCurrent(CurrentDocumentKey,SessionGeneration(CurrentDocumentKey)) && _listener?.HasPendingWork == true;
        internal ClashResolveAI.ClashEngine.ScanStatistics? LastStatistics => _listener?.LastStatistics;
        internal string CurrentDocumentKey => DocumentSession.CurrentKey;
        internal long SessionGeneration(string key) => _gateway?.Generation(key)??0;
        internal bool IsCurrent(string key,long generation) => IsRunning && _gateway?.IsCurrent(key,generation)==true;
        internal void ActivateDocument(string key) {_gateway?.Activate(key);PublishSession(key);}
        internal void CloseDocument(string key) => _gateway?.Close(key);
        internal void SessionRenewed(string key,long generation) {_listener?.SessionRenewed(key,generation);PublishSession();}
        internal void DocumentClosed(string key) => _listener?.DocumentClosed(key);
        public double MaximumLiveApiSliceMilliseconds=>_listener?.MaximumApiSliceMilliseconds??0;
        public LiveScanStatus? ScanStatus=>_listener?.Status;
        internal void FullScanStarted(string key,long revision)=>_listener?.FullScanStarted(key,revision);
        internal void FullScanEnded(Document? doc,string key,bool success,bool complete,ScanScope? scope,long revision)=>_listener?.FullScanEnded(doc,key,success,complete,scope,revision);
        internal void RenewAfterFullScan(string key)=>_gateway?.Renew(key);
        internal void AdvanceCore(string key,long generation) {if(IsCurrent(key,generation))_listener?.Advance(key,generation);}
        internal void CheckSelectionCore() => _listener?.CheckCurrentSelection();
        internal void ExecuteQueued(UIApplication app) => _gateway?.Execute(app); // In-Revit verification only.

        private bool Request(LiveOperation operation,LiveClashDto? clash=null,object? payload=null) =>
            IsRunning && _gateway?.Submit(operation,clash?.NormalizedKey??"",clash?.GeometryRevision??0,payload,clash?.HostDocumentKey,clash?.SessionGeneration)==true;
        public void RequestNavigation(LiveClashDto clash,NavMode mode) => Request(mode==NavMode.View3D?LiveOperation.Navigate3D:LiveOperation.Navigate2D,clash);
        public void RequestInspection(LiveClashDto clash) => Request(LiveOperation.Inspect,clash);
        public void PinInspection(LiveClashDto clash,InspectionScene scene,InspectorPreferences preferences) => Request(LiveOperation.Pin,clash,new RevitLiveMonitorGateway.PinPayload(scene,preferences));
        public void RequestSnapshot(LiveClashDto clash) => Request(LiveOperation.Snapshot,clash);
        public bool RequestScan(LiveScanAction action) => RequestScan(action,CurrentDocumentKey,SessionGeneration(CurrentDocumentKey));
        internal bool RequestScan(LiveScanAction action,string documentKey,long generation) => IsRunning && _gateway?.Submit(action==LiveScanAction.Clear?LiveOperation.Clear:action==LiveScanAction.Cancel?LiveOperation.Cancel:LiveOperation.Refresh,documentKey:documentKey,sessionGeneration:generation)==true;
        public void RecheckLedger(Document doc) {if(IsRunning)_gateway?.Submit(LiveOperation.Refresh,documentKey:DocumentSession.Key(doc));}
        public void CancelRecheck(Document doc) {if(IsRunning)_gateway?.Submit(LiveOperation.Cancel,documentKey:DocumentSession.Key(doc));}
        public void ClearSession(Document doc) {if(IsRunning)_gateway?.Submit(LiveOperation.Clear,documentKey:DocumentSession.Key(doc));}
        internal void RecheckLedgerCore(Document doc) {
            if(!IsRunning||_listener==null)return;
            if(Mode==MonitorMode.Off)return;
            var ids=Mode==MonitorMode.Trigger?new System.Collections.Generic.List<ElementId>():LiveSessionLedger.LiveIds(doc);
            if(ids.Count==0&&(_listener.Status?.QueueDepth??0)==0){ClashRadarPanel.Instance.SetScanStatus(Mode==MonitorMode.Trigger?"No pending changes":"Nothing drawn yet",true);return;}
            _listener.RecheckLedger(doc,ids);
            if(_listener.Status?.RequiresFullScan!=true)
                ClashRadarPanel.Instance.SetScanStatus($"Re-checking {Math.Max(ids.Count,_listener.Status?.QueueDepth??0)} session elements…",false);
        }
        internal void CancelRecheckCore(Document doc) => _listener?.CancelChecks(doc);
        internal void ClearSessionCore(Document doc) {
            _listener?.ClearChecks(doc);LiveSessionLedger.Clear(doc);
            RadarDataStore.Instance.BeginSession(true);
            var status=_listener?.Status;
            ClashRadarPanel.Instance.SetScanStatus(status?.RequiresFullScan==true?status.Reason:"Live session cleared",true);
        }
        public void Start(UIApplication app) {
            if(IsRunning)return;
            if(app.ActiveUIDocument==null)throw new InvalidOperationException("Open a project first.");
            if(app.ActiveUIDocument.Document.IsFamilyDocument)throw new InvalidOperationException("Open a host project first.");
            DocumentSession.Activate(app.ActiveUIDocument.Document);
            try {
                _alert=new AlertSystem();_listener=new EventListener(app,_alert);
                _gateway=new RevitLiveMonitorGateway(this);
                _gateway.Activate(DocumentSession.CurrentKey);
                Mode=MonitorMode.Live;IsRunning=true;_listener.Start();PublishSession();
                if(ScanCoordinator.Busy&&ScanCoordinator.BusyDocumentKey==DocumentSession.CurrentKey)_listener.FullScanStarted(DocumentSession.CurrentKey,ScanCoordinator.StartRevision);
                RewirePanel();Request(LiveOperation.Selection);
                Diagnostics.Log("Document-scoped Live Monitor started");
            }catch {Stop();throw;}
        }
        public void RewirePanel() {
            var panel=ClashRadarPanel.Instance;
            PublishSession();panel.SetActiveState(IsRunning&&Mode!=MonitorMode.Off);panel.Show();
        }
        public void Stop() {
            bool running=IsRunning;IsRunning=false;Mode=MonitorMode.Off;
            _gateway?.Dispose();_gateway=null;
            _listener?.Stop();_listener=null;_alert?.Dispose();_alert=null;
            PublishSession();ClashRadarPanel.Instance.SetActiveState(false);
            Diagnostics.FlushLive(TimeSpan.FromSeconds(2));
            if(running)new ToastWindow("Clash Radar stopped",$"Last session: {RadarDataStore.Instance.TotalDetected} clash(es) detected.","#7F8C8D").ShowForSeconds(3);
        }
        public void ClearHistory() => _listener?.ClearReported();
        public bool CheckCurrentSelection() => Request(LiveOperation.Selection);
        public void ShowStatusToast(bool running) => _alert?.ShowMonitorStatusToast(running);
    }
}

