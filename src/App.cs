// Revit application startup, ribbon registration and shared API services.
using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;
using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClashResolveAI
{
    public class App : IExternalApplication
    {
        public static bool        MonitorActive  { get; set; } = false;
        public static PushButton? MonitorButton  { get; private set; }

        // FIX (Bug 5): Store the Revit main-thread dispatcher at startup.
        // ToastWindow uses this instead of Application.Current?.Dispatcher
        // which can be null inside Revit's host process.
        public static Dispatcher? UIDispatcher   { get; private set; }

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                // Capture the UI thread dispatcher (called on Revit's main thread at startup).
                UIDispatcher = Dispatcher.CurrentDispatcher;
                app.RegisterDockablePane(ClashRadarPanel.PaneId,"Clash Radar",ClashRadarPanel.Instance);

                const string tab = "MEP AI Tools";
                const string pan = "ClashResolve AI 9.4";
                try { app.CreateRibbonTab(tab); } catch { }
                var    rp  = app.CreateRibbonPanel(tab, pan);
                string dll = Assembly.GetExecutingAssembly().Location;

                // Full Scan
                rp.AddItem(Btn("DetectClashes", "Full\nScan", dll,
                    "ClashResolveAI.Commands.DetectClashesCommand",
                    "Full Scan updates the Dashboard. Hard-only by default; optional clearance and linked-model scope.",
                    "detect_16.png", "detect_32.png"));
                rp.AddSeparator();

                // Generate RFIs (BCF + Word + Excel + AI)
                rp.AddItem(Btn("GenerateRFIs", "Generate\nRFIs", dll,
                    "ClashResolveAI.Commands.GenerateRFIsCommand",
                    "Export BCF 2.1 (Navisworks/Solibri), Word RFI report, Excel coordination sheet, AI suggestions.",
                    "rfi_16.png", "rfi_32.png"));
                rp.AddSeparator();

                // Live Monitor / Clash Radar
                var monData = Btn("LiveMonitor", "Live\nMonitor", dll,
                    "ClashResolveAI.Commands.LiveMonitorCommand",
                    "Open dockable Clash Radar: Live checks automatically, Trigger checks on demand, Off disables live checking.",
                    "monitor_16.png", "monitor_32.png");
                MonitorButton = rp.AddItem(monData) as PushButton;
                rp.AddSeparator();

                // Dashboard
                rp.AddItem(Btn("Dashboard", "Dashboard", dll,
                    "ClashResolveAI.Commands.DashboardCommand",
                    "Professional BIM Coordination Dashboard:\nGrouped issues, discipline matrix, trend analytics, lifecycle management.",
                    "dashboard_16.png", "dashboard_32.png"));
                rp.AddSeparator();

                // Settings
                rp.AddItem(Btn("Settings", "Settings", dll,
                    "ClashResolveAI.Commands.SettingsCommand",
                    "API key, rule sets (JSON), clearance values, linked model options, coordination zones.",
                    "settings_16.png", "settings_32.png"));

                Diagnostics.Log("Startup "+Assembly.GetExecutingAssembly().FullName+" from "+dll);
                app.ViewActivated+=(_,e)=>{try{if(e.CurrentActiveView?.Document!=null)DocumentSession.Activate(e.CurrentActiveView.Document);}catch(Exception ex){Diagnostics.Log("Activate project",ex);}};
                // DocumentClosing can be cancelled; clean up only after native closure.
                app.Idling+=(_,__)=>{try{DocumentSession.PruneClosedDocuments();}catch(Exception ex){Diagnostics.Log("Closed project cleanup",ex);}};
                ScanCoordinator.Attach(app);
                IntegrationVerification.Attach(app);
                ProductionVerification.Attach(app);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ClashResolve AI 9.4 — Startup Error", ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            ScanCoordinator.Shutdown();
            LiveMonitor.LiveMonitorService.Instance?.Stop();
            ClashRadarPanel.Instance.Shutdown();
            ClashDatabase.Instance?.Dispose();
            return Result.Succeeded;
        }

        public static void RefreshMonitorButton()
        {
            if (MonitorButton == null) return;
            if (MonitorActive)
            {
                MonitorButton.ToolTip    = "Open Clash Radar · "+LiveMonitorService.Instance.Mode+" mode. Choose Off in the pane to disable live checking.";
                MonitorButton.LargeImage = I("alert_32.png");
                MonitorButton.Image      = I("alert_16.png");
            }
            else
            {
                MonitorButton.ToolTip    = "Open Clash Radar. Start monitoring or choose Live / Trigger / Off in the pane.";
                MonitorButton.LargeImage = I("monitor_32.png");
                MonitorButton.Image      = I("monitor_16.png");
            }
        }

        private static PushButtonData Btn(string name, string text, string dll,
            string cls, string tip, string img16, string img32) =>
            new PushButtonData(name, text, dll, cls)
            {
                ToolTip    = tip,
                Image      = I(img16),
                LargeImage = I(img32)
            };

        internal static BitmapImage? I(string fileName)
        {
            try
            {
                var assembly=Assembly.GetExecutingAssembly();
                using(var stream=assembly.GetManifestResourceStream("ClashResolveAI.Resources."+fileName))
                {
                    if(stream!=null){var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();return image;}
                }
                string asmName = assembly.GetName().Name!;
                var uri = new Uri(
                    $"pack://application:,,,/{asmName};component/Resources/{fileName}",
                    UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource   = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { }

            try
            {
                string dir  = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
                string path = Path.Combine(dir, "Resources", fileName);
                if (!File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource   = new Uri(path);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}

