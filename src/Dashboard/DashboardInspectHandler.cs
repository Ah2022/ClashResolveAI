using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using ClashResolveAI.Inspection;
using System.Windows;
using System.Windows.Media;
namespace ClashResolveAI.Dashboard
{
    internal sealed class DashboardInspectHandler : IExternalEventHandler
    {
        internal ClashResult? Pending;
        internal InspectionScene? PinScene;
        internal InspectorPreferences? PinPreferences;
        internal System.Action<ClashResult,InspectionScene,InspectorPreferences>? RaisePin;
        public string GetName()=>"Dashboard geometry inspection";
        public void Execute(UIApplication app)
        {
            var row=Pending;var pin=PinScene;var preferences=PinPreferences;Pending=null;PinScene=null;PinPreferences=null;
            if(row==null)return;
            if(ScanCoordinator.Busy||ScanCoordinator.IsStale(row)){TaskDialog.Show("Full Scan required","Run Full Scan before inspecting changed or restored issues.");return;}
            InspectionHandler.Run(app,row,pin,preferences,(scene,error)=>{
                if(scene==null){TaskDialog.Show("Issue inspection",error);return;}
                var inspector=new ClashInspector(InspectorPreferences.Load());inspector.Display(scene);
                inspector.NavigateRequested+=three=>{if(three)ClashDashboard.Instance.Show3DAction?.Invoke(row);else ClashDashboard.Instance.Show2DAction?.Invoke(row);};
                inspector.PinRequested+=s=>RaisePin?.Invoke(row,s,inspector.Preferences);
                new Window {Title="Full Scan issue inspector · "+row.ClashId,Width=1050,Height=760,MinWidth=650,MinHeight=480,Background=new SolidColorBrush(Color.FromRgb(19,27,40)),Content=inspector}.Show();
            });
        }
    }
}
