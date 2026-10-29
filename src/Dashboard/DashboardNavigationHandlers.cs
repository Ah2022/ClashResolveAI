using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using ClashResolveAI.Services;
using System;
namespace ClashResolveAI.Dashboard
{
    public class DashboardShow2DHandler : IExternalEventHandler
    {
        public volatile ClashResult? Pending;
        public void Execute(UIApplication app) { var clash = Pending; Pending = null; if(clash == null) return; if(ScanCoordinator.IsStale(clash)){TaskDialog.Show("Full Scan required","Run Full Scan to verify restored or changed results before navigating.");return;} try { ClashViewNavigation.Show(app, clash, false); } catch(Exception ex) { Diagnostics.Log("Dashboard 2D navigation", ex); } }
        public string GetName() => "Dashboard Show 2D";
    }
    public class DashboardShow3DHandler : IExternalEventHandler
    {
        public volatile ClashResult? Pending;
        public void Execute(UIApplication app) { var clash = Pending; Pending = null; if(clash == null) return; if(ScanCoordinator.IsStale(clash)){TaskDialog.Show("Full Scan required","Run Full Scan to verify restored or changed results before navigating.");return;} try { ClashViewNavigation.Show(app, clash, true); } catch(Exception ex) { Diagnostics.Log("Dashboard 3D navigation", ex); } }
        public string GetName() => "Dashboard Show 3D";
    }
}
