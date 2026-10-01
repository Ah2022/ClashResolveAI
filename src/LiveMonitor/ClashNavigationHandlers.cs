// LiveMonitor/ClashNavigationHandlers.cs  — v5.0
//
// All ExternalEvent handlers used by ClashRadarPanel buttons.
// ExternalEvent is the ONLY safe way to call Revit API from WPF button handlers.
//
// Handlers:
//   ClashNavHandler    — Show 3D or Show 2D view, zoomed to clash elements
//   ClashRefreshHandler— Re-scan changed elements, prune resolved clashes
//   ClashExportHandler — Export current radar list to CSV

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace ClashResolveAI.LiveMonitor
{
    // ══════════════════════════════════════════════════════════════════
    //  NAVIGATE TO CLASH  (Show 3D / Show 2D)
    // ══════════════════════════════════════════════════════════════════

    public enum NavMode { View3D, View2D }

    public class ClashNavHandler : IExternalEventHandler
    {
        public ClashResult? Target { get; set; }
        public NavMode Mode { get; set; } = NavMode.View3D;
        public long LastRequestedViewId { get; private set; } = -1;
        public void Execute(UIApplication app)
        {
            if (Target == null) return;
            try { LastRequestedViewId = Services.ClashViewNavigation.Show(app, Target, Mode == NavMode.View3D); }
            catch (Exception ex) { Diagnostics.Log("Clash navigation failed", ex); }
        }
        public string GetName() => "ClashResolveAI_RadarNav";
    }

    public class ClashRefreshHandler : IExternalEventHandler
    {
        public enum Request { Ledger, Clear, Cancel }
        public Request Action {get;set;}
        public void Execute(UIApplication app)
        {
            var action=Action;Action=Request.Ledger;
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument){ClashRadarPanel.Instance.SetScanStatus("Open a project first",true);return;}
            try {
                DocumentSession.Activate(doc);
                if(action==Request.Clear){LiveMonitorService.Instance.ClearSession(doc);return;}
                if(action==Request.Cancel){LiveMonitorService.Instance.CancelRecheck(doc);return;}
                if(action==Request.Ledger){LiveMonitorService.Instance.RecheckLedger(doc);return;}

            } catch (Exception ex) { Diagnostics.Log("Re-check failed", ex);ClashRadarPanel.Instance.SetScanStatus(ex.Message,true); }
        }

        public string GetName() => "ClashResolveAI_RadarRefresh";
    }

    // ══════════════════════════════════════════════════════════════════
    //  EXPORT  — Dump active radar list to CSV (runs on UI thread via dialog)
    //  No Revit API needed — pure file I/O, safe to call directly.
    // ══════════════════════════════════════════════════════════════════

    public static class RadarExporter
    {
        public static void ExportToCsv(List<ClashResult> clashes)
        {
            if (!clashes.Any())
            {
                MessageBox.Show("No active clashes to export.", "Clash Radar");
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Title    = "Export Clash Radar List",
                Filter   = "CSV files (*.csv)|*.csv",
                FileName = $"ClashRadar_{DateTime.Now:yyyyMMdd_HHmm}.csv"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            var sb = new StringBuilder();
            sb.AppendLine("#,Time,Category A,Category B,ID A,ID B,Severity,Gap(mm),Location,GridRef,Origin,LiveSessionId");

            int i = 1;
            foreach (var c in clashes)
            {
                string catA  = c.CategoryNameA;
                string catB  = c.CategoryNameB;
                long idAVal  = c.ElementAId;
                long idBVal  = c.ElementBId;
                string idA   = idAVal >= 0 ? idAVal.ToString() : "";
                string idB   = idBVal >= 0 ? idBVal.ToString() : "";
                string sev   = c.Severity.ToString();
                string loc   = string.IsNullOrEmpty(c.LocationText) ? c.ZoneName : c.LocationText;
                sb.AppendLine($"{i++},{DateTime.Now:HH:mm},{Q(catA)},{Q(catB)},{idA},{idB},{sev},{c.GapMM:F1},{Q(loc)},{Q(c.GridRef)},{c.Origin},{Q(c.LiveSessionId)}");
            }

            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            System.Diagnostics.Process.Start(dlg.FileName);
        }

        private static string Q(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
    }
}

