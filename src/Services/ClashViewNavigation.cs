using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ClashResolveAI.Core;
using System;
using System.Linq;

namespace ClashResolveAI.Services
{
    internal static class ClashViewNavigation
    {
        private static int _request;
        public static long Show(UIApplication app, ClashResult clash, bool threeD, Func<bool>? liveRequestValid = null)
        {
            var ui = app.ActiveUIDocument;
            var doc = ui?.Document;
            if (doc == null || !DocumentSession.Matches(clash, doc))
                throw new InvalidOperationException("This clash belongs to another project. Activate that project first.");
            var ids = clash.GetHostSelectionIds(doc);
            if (ids.Count == 0) throw new InvalidOperationException("Clash elements have been removed. Refresh the clash list.");
            View view;
            if (threeD)
            {
                using var tx = new Transaction(doc, "ClashResolve — Navigate 3D");
                tx.Start();
                var v3 = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                    .FirstOrDefault(v => !v.IsTemplate && v.Name == "Clash_Radar_3D");
                if (v3 == null)
                {
                    var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                        .First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
                    v3 = View3D.CreateIsometric(doc, type.Id);
                    v3.Name = "Clash_Radar_3D";
                }
                var center = clash.ClashPoint;
                var margin = new XYZ(3, 3, 3);
                v3.SetSectionBox(new BoundingBoxXYZ { Min = center - margin, Max = center + margin });
                v3.IsSectionBoxActive = true;
                if (v3.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate))
                    v3.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                v3.IsolateElementsTemporary(ids);
                var eye = center + new XYZ(-8, -8, 6);
                var forward = (center - eye).Normalize();
                var up = forward.CrossProduct(XYZ.BasisZ).CrossProduct(forward).Normalize();
                v3.SetOrientation(new ViewOrientation3D(eye, up, forward));
                v3.DetailLevel = ViewDetailLevel.Fine;
                v3.DisplayStyle = DisplayStyle.ShadingWithEdges;
                if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Could not create the clash view.");
                view = v3;
            }
            else
            {
                var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l=>Math.Abs(l.Elevation-clash.ClashPoint.Z)).FirstOrDefault()
                    ?? throw new InvalidOperationException("Create a level before using Show 2D.");
                using var tx=new Transaction(doc,"ClashResolve — Navigate 2D");
                tx.Start();
                var plan=new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v=>!v.IsTemplate&&v.Name=="Clash_Radar_2D_"+level.Id.Value);
                if(plan==null){
                    var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.FloorPlan);
                    plan=ViewPlan.Create(doc,type.Id,level.Id);plan.Name="Clash_Radar_2D_"+level.Id.Value;
                }
                double z=clash.ClashPoint.Z-level.Elevation;
                using(var range=plan.GetViewRange()){
                    foreach(var plane in new[]{PlanViewPlane.TopClipPlane,PlanViewPlane.CutPlane,PlanViewPlane.BottomClipPlane,PlanViewPlane.ViewDepthPlane})range.SetLevelId(plane,level.Id);
                    range.SetOffset(PlanViewPlane.TopClipPlane,z+4);range.SetOffset(PlanViewPlane.CutPlane,z+0.1);
                    range.SetOffset(PlanViewPlane.BottomClipPlane,z-4);range.SetOffset(PlanViewPlane.ViewDepthPlane,z-4);plan.SetViewRange(range);
                }
                if(plan.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate))plan.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                plan.IsolateElementsTemporary(ids);plan.DetailLevel=ViewDetailLevel.Fine;
                tx.Commit();view=plan;
            }

            // Revit processes view requests after the API callback returns. Zoom and
            // selection must also wait for that view to become active.
            ui!.RequestViewChange(view);
            long viewId = view.Id.Value;
            int request = ++_request;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            EventHandler<IdlingEventArgs>? complete = null;
            complete = (_, args) =>
            {
                try
                {
                    var current = app.ActiveUIDocument;
                    if (request != _request || liveRequestValid?.Invoke()==false || DateTime.UtcNow > deadline || current == null || !DocumentSession.Matches(clash, current.Document))
                    { app.Idling -= complete; return; }
                    if (current.ActiveView?.Id.Value != viewId) { args.SetRaiseWithoutDelay(); return; }
                    app.Idling -= complete;
                    current.Selection.SetElementIds(ids);
                    var window = current.GetOpenUIViews().FirstOrDefault(v => v.ViewId.Value == viewId);
                    var pad = new XYZ(4, 4, 4);
                    window?.ZoomAndCenterRectangle(clash.ClashPoint - pad, clash.ClashPoint + pad);
                    Diagnostics.Log("Navigation completed: " + viewId);
                }
                catch (Exception ex) { app.Idling -= complete; Diagnostics.Log("Navigation completion failed", ex); }
            };
            app.Idling += complete;
            return viewId;
        }
    }
}
