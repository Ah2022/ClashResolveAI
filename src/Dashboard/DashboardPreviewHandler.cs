using System;
using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using ClashResolveAI.Inspection;
namespace ClashResolveAI.Dashboard
{
    internal sealed class DashboardPreviewHandler:IExternalEventHandler
    {
        internal ClashResult? Pending;
        internal Action<InspectionScene?,string>? Completed;
        public string GetName()=>"Dashboard panel preview";
        public void Execute(UIApplication app)
        {
            var row=Pending;var completed=Completed;Pending=null;Completed=null;
            if(row==null)return;
            if(ScanCoordinator.Busy||ScanCoordinator.FullResultsStale||ScanCoordinator.IsStale(row)){completed?.Invoke(null,"Run Full Scan to refresh this preview.");return;}
            InspectionHandler.Run(app,row,null,null,completed);
        }
    }
}
