using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ClashResolveAI.Core;

namespace ClashResolveAI.LiveMonitor
{
    internal sealed class RadarActions : IRadarActions
    {
        private LiveMonitorService Service=>LiveMonitorService.Instance;
        public bool IsUsable(LiveClashDto row)=>Service.IsCurrent(row.HostDocumentKey,row.SessionGeneration)&&!ScanCoordinator.IsStale(row);
        public bool Check(string doc,long generation,LiveScanAction action)=>Service.RequestScan(action,doc,generation);
        public void Mode(string doc,long generation,MonitorMode mode)=>Service.RequestMode(mode,doc,generation);
        public void Navigate(LiveClashDto row,bool threeD)=>Service.RequestNavigation(row,threeD?NavMode.View3D:NavMode.View2D);
        public void Inspect(LiveClashDto row)=>Service.RequestInspection(row);
        public void Pin(LiveClashDto row,RadarPinRequest request)=>Service.PinInspection(row,request.Scene,request.Preferences);
        public void Export(IReadOnlyList<LiveClashDto> rows)=>RadarExporter.ExportToCsv(rows.ToList());
        public void Hide(string doc,long generation)=>Service.RequestPane(false,doc,generation);
        public void ExportDiagnostics(string document)=>Diagnostics.ExportLive(document);
        public bool ConfirmPurge()=>MessageBox.Show("Purge non-hard results (including possible hard) from this project's live history?","Clash Radar",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;
    }
}
