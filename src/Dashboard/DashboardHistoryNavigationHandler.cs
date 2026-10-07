using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using ClashResolveAI.Dashboard.Domain;
using System;
using System.Collections.Generic;
namespace ClashResolveAI.Dashboard
{
    internal sealed class DashboardHistoryNavigationHandler:IExternalEventHandler
    {
        internal ClashObservation? Pending;
        public string GetName()=>"Select current components from scan history";
        public void Execute(UIApplication app)
        {
            var row=Pending;Pending=null;if(row==null)return;
            try {
                var doc=app.ActiveUIDocument?.Document??throw new InvalidOperationException("Open the issue project first.");
                if(DocumentSession.Key(doc)!=row.DocumentKey)throw new InvalidOperationException("The active project changed. Activate the historical issue project first.");
                var selection=new List<ElementId>();
                void Resolve(string unique,string link){
                    if(unique=="")throw new InvalidOperationException("This historical component lacks a stable UniqueId; its current identity cannot be verified.");
                    RevitLinkInstance? instance=null;var target=doc;
                    if(link!=""){instance=doc.GetElement(link) as RevitLinkInstance??throw new InvalidOperationException("The historical linked-model instance was deleted or replaced.");target=instance.GetLinkDocument()??throw new InvalidOperationException("The linked model is unloaded. Load it before selecting its current components.");}
                    var element=target.GetElement(unique)??throw new InvalidOperationException("A historical component was deleted or replaced. Historical evidence is retained.");selection.Add(instance?.Id??element.Id);
                }
                Resolve(row.ElementUniqueIdA,row.LinkInstanceA);Resolve(row.ElementUniqueIdB,row.LinkInstanceB);
                app.ActiveUIDocument!.Selection.SetElementIds(selection);app.ActiveUIDocument.ShowElements(selection);
            }catch(Exception ex){TaskDialog.Show("Current component selection",ex.Message);}
        }
    }
}
