// Live Monitor owns EventListener and the Radar's API events.
// DocumentChanged/selection queue local jobs through the shared UnifiedScan engine.
// Full Scan owns the Dashboard; it is independent of Radar lifecycle and controls.

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Alert;
using ClashResolveAI.Core;
using ClashResolveAI.Events;
using System;
using System.Diagnostics;
using System.Linq;


namespace ClashResolveAI.LiveMonitor
{
    public class LiveMonitorService
    {
        // ── Singleton ──────────────────────────────────────────────────
        private static LiveMonitorService? _instance;
        public  static LiveMonitorService   Instance =>
            _instance ?? (_instance = new LiveMonitorService());

        // ── Components ─────────────────────────────────────────────────
        private ClashResolveAI.Inspection.InspectionHandler? _inspectionHandler;
        private ExternalEvent? _inspectionEvent;
        public void RequestInspection(ClashResult clash)
        {
            if(!IsRunning||_inspectionHandler==null)return;
            _inspectionHandler.Pending=clash;_inspectionHandler.PinScene=null;_inspectionHandler.PinPreferences=null;_inspectionEvent?.Raise();
        }
        public void PinInspection(ClashResult clash,ClashResolveAI.Inspection.InspectionScene scene,ClashResolveAI.Inspection.InspectorPreferences preferences)
        {
            if(!IsRunning||_inspectionHandler==null)return;
            _inspectionHandler.Pending=clash;_inspectionHandler.PinScene=scene;
            _inspectionHandler.PinPreferences=new ClashResolveAI.Inspection.InspectorPreferences { PaddingFeet=preferences.PaddingFeet,Focus=preferences.Focus,Context=preferences.Context,Corner=preferences.Corner,AxisB=preferences.AxisB };
            _inspectionEvent?.Raise();
        }
        private EventListener?    _listener;
        private AlertSystem?      _alert;
        // Panel wiring — kept as fields for RewirePanel()
        private ExternalEvent?       _navEvent;
        private ExternalEvent?       _refreshEvent;
        private ClashNavHandler?     _navHandler;
        private ClashRefreshHandler? _refreshHandler;

        // v9.0: snapshot capture ExternalEvent + handler
        private ExternalEvent?        _snapEvent;
        private ClashSnapshotHandler? _snapHandler;

        /// <summary>
        /// Requests a real-image snapshot of the given clash on the Revit API thread.
        /// Result is published via ClashSnapshotStore.Instance.SnapshotReady.
        /// </summary>
        public void RequestSnapshot(ClashResult clash)
        {
            if (!IsRunning || _snapHandler == null || _snapEvent == null) return;
            _snapHandler.RequestSnapshot(clash);
            _snapEvent.Raise();
        }

        public bool IsRunning { get; private set; }
        internal bool HasPendingWork => _listener?.HasPendingWork == true;
        internal ClashResolveAI.ClashEngine.ScanStatistics? LastStatistics=>_listener?.LastStatistics;
        public void RecheckLedger(Document doc)
        {
            if(!IsRunning||_listener==null){ClashRadarPanel.Instance.SetScanStatus("Start Live Monitor to record a session",true);return;}
            DocumentSession.Activate(doc);
            var ids=LiveSessionLedger.LiveIds(doc);
            if(ids.Count==0){ClashRadarPanel.Instance.SetScanStatus("Nothing drawn yet",true);return;}
            _listener.RecheckLedger(doc,ids);
            ClashRadarPanel.Instance.SetScanStatus($"Re-checking {ids.Count} session elements…",false);
        }
        public void CancelRecheck(Document doc)=>_listener?.CancelChecks(doc);
        public void ClearSession(Document doc)
        { _listener?.CancelChecks(doc);LiveSessionLedger.Clear(doc);RadarDataStore.Instance.BeginSession(true);ClashRadarPanel.Instance.SetScanStatus("Live session cleared",true); }

        // ══════════════════════════════════════════════════════════════
        //  START — activate Clash Radar
        // ══════════════════════════════════════════════════════════════

        public void Start(UIApplication app)
        {
            if (IsRunning) return;
            if (app.ActiveUIDocument == null) throw new InvalidOperationException("Open a project first.");
            DocumentSession.Activate(app.ActiveUIDocument.Document);

            // ── 1. Initialise components ───────────────────────────────
            _alert    = new AlertSystem();
            _listener = new EventListener(app, _alert);
            _listener.Start();

            // ── 2. Create ExternalEvent handlers ──────────────────────
            _navHandler     = new ClashNavHandler();
            _refreshHandler = new ClashRefreshHandler();
            _navEvent       = ExternalEvent.Create(_navHandler);
            _refreshEvent   = ExternalEvent.Create(_refreshHandler);

            // v9.0: snapshot capture handler
            _snapHandler = new ClashSnapshotHandler();
            _snapEvent   = ExternalEvent.Create(_snapHandler);
            _inspectionHandler=new ClashResolveAI.Inspection.InspectionHandler();
            _inspectionEvent=ExternalEvent.Create(_inspectionHandler);

            // ── 3. Create & wire the radar panel ─────────────────────
            var panel = ClashRadarPanel.Instance;
            panel.SetRevitEvents(_navEvent, _refreshEvent, _navHandler, _refreshHandler);
            panel.SetActiveState(true);
            panel.Show();

            IsRunning = true;
            Debug.WriteLine("[ClashRadar] v7.0 Started.");

            // ── 4. Initial scan on current selection ──────────────────
            RunInitialScan(app);
        }

        // ══════════════════════════════════════════════════════════════
        //  REWIRE PANEL — re-attach live events to a fresh panel
        // ══════════════════════════════════════════════════════════════

        public void RewirePanel()
        {
            if (_navEvent == null || _refreshEvent == null ||
                _navHandler == null || _refreshHandler == null) return;

            var panel = ClashRadarPanel.Instance;
            panel.SetRevitEvents(_navEvent, _refreshEvent, _navHandler, _refreshHandler);
            panel.SetActiveState(IsRunning);
            panel.Show();
            Debug.WriteLine("[ClashRadar] Panel re-wired and shown.");
        }

        // ══════════════════════════════════════════════════════════════
        //  STOP — deactivate Clash Radar
        // ══════════════════════════════════════════════════════════════

        public void Stop()
        {
            if (!IsRunning) return;

            _listener?.Stop();
            _listener    = null;
            _alert?.Dispose();
            _alert       = null;
            _inspectionEvent?.Dispose();_inspectionEvent=null;_inspectionHandler=null;
            _snapHandler = null;
            _snapEvent?.Dispose();
            _snapEvent   = null;
            _navEvent?.Dispose(); _navEvent = null;
            _refreshEvent?.Dispose(); _refreshEvent = null;

            if (ClashRadarPanel.IsVisible)
                ClashRadarPanel.Instance.SetActiveState(false);

            new ToastWindow("Clash Radar stopped",
                $"Last session: {RadarDataStore.Instance.TotalDetected} clash(es) detected.",
                "#7F8C8D").ShowForSeconds(3);

            IsRunning = false;
            Debug.WriteLine("[ClashRadar] Stopped.");
        }

        // ══════════════════════════════════════════════════════════════
        //  INITIAL SCAN — populate panel on Start
        // ══════════════════════════════════════════════════════════════

        private void RunInitialScan(UIApplication app)
        { _listener?.CheckCurrentSelection(); }

        // ── Delegated helpers ──────────────────────────────────────────
        public void ClearHistory()          => _listener?.ClearReported();
        public bool CheckCurrentSelection() => _listener?.CheckCurrentSelection() == true;
        public void ShowStatusToast(bool running) => _alert?.ShowMonitorStatusToast(running);

        // NOTE: ResetInternal() REMOVED in v7.0 — replaced by tx-name guard.
    }
}

