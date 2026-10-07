using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using System;
namespace ClashResolveAI.Dashboard
{
    internal sealed class DashboardRunScanHandler : IExternalEventHandler
    {
        public string DocumentKey {get;set;}="";
        public string GetName()=>"Dashboard Full Scan";
        public void Execute(UIApplication app)
        {
            try {
                var doc=app.ActiveUIDocument?.Document;
                if(doc==null||DocumentSession.Key(doc)!=DocumentKey){TaskDialog.Show("Full Scan","The active project changed. Refresh the dashboard before scanning.");return;}
                if(ScanCoordinator.Busy){TaskDialog.Show("Full Scan","A scan is already running.");return;}
                string message="";var result=Commands.DetectClashesCommand.Run(app,ref message);
                if(result==Result.Failed)TaskDialog.Show("Full Scan",message);
            }catch(Exception ex){TaskDialog.Show("Full Scan",ex.Message);}
        }
    }
}
