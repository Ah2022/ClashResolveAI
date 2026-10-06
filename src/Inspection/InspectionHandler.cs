using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;
using System;
using System.Linq;

namespace ClashResolveAI.Inspection
{
    internal static class InspectionHandler
    {
        internal static void Run(UIApplication app, ClashResult clash, InspectionScene? scene, InspectorPreferences? preferences, Action<InspectionScene?,string>? completed = null)
        {
            try
            {
                var doc=app.ActiveUIDocument?.Document;
                if(doc==null||!DocumentSession.Matches(clash,doc))throw new InvalidOperationException("Activate the clash project first.");
                if(scene!=null&&preferences!=null)
                {
                    if(ScanCoordinator.IsStale(clash))throw new InvalidOperationException("These elements changed. Re-check before pinning this inspection.");
                    using var tx=new Transaction(doc,"ClashResolve — Pin inspection view");tx.Start();
                    var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.ThreeDimensional);
                    var view=View3D.CreateIsometric(doc,type.Id);view.Name="Clash_"+clash.ClashId+"_"+DateTime.Now.ToString("HHmmssfff");
                    scene.Frame(preferences.PaddingFeet,preferences.Focus,out var min,out var max);
                    view.SetSectionBox(new BoundingBoxXYZ { Min=InspectionGeometry.XYZ(min),Max=InspectionGeometry.XYZ(max) });view.IsSectionBoxActive=true;
                    scene.Axes("3d",preferences.Corner,preferences.AxisB,out _,out var up,out var forward);
                    view.SetOrientation(new ViewOrientation3D(InspectionGeometry.XYZ((min+max)*.5-forward*Math.Max(10,(max-min).Length*3)),InspectionGeometry.XYZ(up),InspectionGeometry.XYZ(forward)));
                    if(!preferences.Context){view.IsolateElementsTemporary(clash.GetHostSelectionIds(doc));view.ConvertTemporaryHideIsolateToPermanent();}
                    view.DetailLevel=ViewDetailLevel.Fine;view.DisplayStyle=DisplayStyle.ShadingWithEdges;
                    tx.Commit();app.ActiveUIDocument!.RequestViewChange(view);
                    return;
                }
                // Geometry is copied into plain coordinates; rendering never calls Revit.
                var built=InspectionGeometry.Build(doc,clash,20);
                completed?.Invoke(built,"");
            }
            catch(Exception ex)
            {
                Diagnostics.Log("Inspection",ex);
                completed?.Invoke(null,ex.Message);
            }
        }

    }
}
