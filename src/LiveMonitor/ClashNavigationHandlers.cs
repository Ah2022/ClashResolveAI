// LiveMonitor/ClashNavigationHandlers.cs  — v5.0
//
// Navigation and scan controls are dispatched by RevitLiveMonitorGateway.
// This file retains the plain-data CSV exporter and navigation mode.

using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace ClashResolveAI.LiveMonitor
{
    public enum NavMode { View3D, View2D }

    // ══════════════════════════════════════════════════════════════════
    //  EXPORT  — Dump active radar list to CSV (runs on UI thread via dialog)
    //  No Revit API needed — pure file I/O, safe to call directly.
    // ══════════════════════════════════════════════════════════════════

    public static class RadarExporter
    {
        public static void ExportToCsv(List<LiveClashDto> clashes)
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

            WriteCsv(clashes,dlg.FileName);
            System.Diagnostics.Process.Start(dlg.FileName);
        }

        internal static void WriteCsv(IReadOnlyList<LiveClashDto> clashes,string path)
        {

            var sb = new StringBuilder();
            sb.AppendLine("#,Time,Category A,Category B,ID A,ID B,Severity,Gap(mm),Location,GridRef,Origin,LiveSessionId,Verification,VerificationReason");

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
                sb.AppendLine($"{i++},{DateTime.Now:HH:mm},{Q(catA)},{Q(catB)},{idA},{idB},{sev},{c.GapMM:F1},{Q(loc)},{Q(c.GridRef)},{c.Origin},{Q(c.LiveSessionId)},{c.Verification},{Q(c.VerificationReason)}");
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static string Q(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
    }
}

