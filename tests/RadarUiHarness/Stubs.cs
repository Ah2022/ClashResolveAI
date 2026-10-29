using System;
using System.Collections.Generic;
using System.Windows;
using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;

namespace Autodesk.Revit.UI
{
    public sealed class DockablePaneId {public Guid Id;public DockablePaneId(Guid id){Id=id;}}
    public enum DockPosition { Right }
    public sealed class DockablePaneState {public DockPosition DockPosition;}
    public sealed class DockablePaneProviderData {public FrameworkElement? FrameworkElement;public DockablePaneState? InitialState;}
    public interface IDockablePaneProvider {void SetupDockablePane(DockablePaneProviderData data);}
    public sealed class DockablePane {public bool Visible;public void Show(){Visible=true;}public void Hide(){Visible=false;}}
    public sealed class UIApplication {public DockablePane Pane=new DockablePane();public DockablePane GetDockablePane(DockablePaneId id)=>Pane;}
}
namespace ClashResolveAI.LiveMonitor
{
    public sealed class LiveMonitorService {public static LiveMonitorService Instance=new LiveMonitorService();public void ShowRadar(){} }
    public static class LiveSessionLedger {public static int Count=>0;}
    internal sealed class RadarActions : IRadarActions
    {
        public bool IsUsable(LiveClashDto row)=>true;
        public bool Check(string doc,long generation,LiveScanAction action)=>true;
        public void Mode(string doc,long generation,MonitorMode mode){ClashRadarPanel.Instance.ViewModel.SetSession(doc,generation,true,mode);}
        public void Navigate(LiveClashDto row,bool threeD){}
        public void Inspect(LiveClashDto row){}
        public void Pin(LiveClashDto row,RadarPinRequest request){}
        public void Export(IReadOnlyList<LiveClashDto> rows){}
        public void Hide(string doc,long generation){}
        public void ExportDiagnostics(string document){}
        public bool ConfirmPurge()=>false;
    }
}
