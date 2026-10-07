using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using ClashResolveAI.Dashboard;
using ClashResolveAI.LiveMonitor;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ClashResolveAI.Core
{
    internal static partial class IntegrationVerification
    {
        private static ElementId? _ledgerPipe,_ledgerSecond,_ledgerBaseline;
        private static ClashResult? _unrelated;
        private static ClashResolveAI.ClashEngine.ScanStatistics? _previousLive;
        private static int _ledgerCount;
        private static readonly List<object> LedgerEvents=new List<object>();
        private static readonly HashSet<string> PlacementEvents=new HashSet<string>();
        private static readonly HashSet<long> AddedFittings=new HashSet<long>();
        private static void RecordLedgerEvent(object sender,DocumentChangedEventArgs e)
        {
            if(_test==null||DocumentSession.Key(e.GetDocument())!=DocumentSession.Key(_test))return;
            var names=e.GetTransactionNames().ToList();foreach(var name in names)if(name.StartsWith("Phase4 place segment"))PlacementEvents.Add(name);
            var added=e.GetAddedElementIds().Select(id=>new {id=id.Value,category=e.GetDocument().GetElement(id)?.Category?.Name}).ToList();
            foreach(var id in e.GetAddedElementIds())if(e.GetDocument().GetElement(id)?.Category?.Id.Value==(long)BuiltInCategory.OST_PipeFitting)AddedFittings.Add(id.Value);
            LedgerEvents.Add(new {names,added,modified=e.GetModifiedElementIds().Select(id=>id.Value).ToList(),deleted=e.GetDeletedElementIds().Select(id=>id.Value).ToList()});
            File.WriteAllText(Path.Combine(_folder,"phase4-document-events.json"),JsonConvert.SerializeObject(LedgerEvents,Formatting.Indented));
        }
        private static void LedgerStep(UIApplication app)
        {
            var doc=_test!;var service=LiveMonitorService.Instance;
            void Next(int step){_step=step;_next=DateTime.UtcNow.AddSeconds(2);}
            bool Pending()=>service.HasPendingWork||ScanCoordinator.Busy;
            if(_step==400){
                using(var tx=new Transaction(doc,"Phase4 baseline pipes")){
                    tx.Start();_ledgerBaseline=NativePipe(doc,new XYZ(195,0,3),new XYZ(205,0,3)).Id;tx.Commit();
                }
                DocumentSession.Activate(doc);ClashDashboard.Instance.RestoreSession(new List<ClashResult>(),new ResultViewFilter());
                ScanCoordinator.Start(doc,"",null,(rows,stats)=>ClashDashboard.Instance.MergeFullScan(rows,stats.Mode,stats.Scope));Next(401);return;
            }
            if(Pending()){
                if(DateTime.UtcNow-_next>TimeSpan.FromMinutes(3))throw new InvalidOperationException("Live verification timed out at step "+_step);
                return;
            }
            if(_step==401){
                _unrelated=ClashDashboard.Instance.Clashes.First(c=>c.TestType==ClashTestType.HardClash);
                app.ActiveUIDocument.Selection.SetElementIds(new ElementId[0]);
                service.Start(app);service.ClearSession(doc);service.ExecuteQueued(app);Next(411);return;
            }
            if(_step==411){
                LiveMonitorService.Instance.RequestScan(LiveScanAction.Ledger);LiveMonitorService.Instance.ExecuteQueued(app);
                DrainUi(ClashRadarPanel.Instance);
                Check(Descendants<TextBlock>(ClashRadarPanel.Instance).Any(t=>t.Text=="Nothing drawn yet"),"Empty ledger finishes with Nothing drawn yet");
                app.Application.DocumentChanged+=RecordLedgerEvent;
                using(var tx=new Transaction(doc,"Phase4 place segment 1")){tx.Start();_ledgerPipe=NativePipe(doc,new XYZ(200,-5,3),new XYZ(200,5,3)).Id;tx.Commit();}
                Next(402);return;
            }
            if(_step==402){
                Check(!ScanCoordinator.IsStale(_unrelated!),"An unrelated clash stays current after drawing a pipe");
                var scene=Inspection.InspectionGeometry.Build(doc,_unrelated!,20);
                Inspection.InspectionHandler.Run(app,_unrelated!,scene,new Inspection.InspectorPreferences {Context=true});
                Check(new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Any(v=>v.Name.StartsWith("Clash_"+_unrelated!.ClashId+"_")),"Unrelated clash remains pinnable after a live edit");
                using(var tx=new Transaction(doc,"Phase4 place segment 2")){
                    tx.Start();var first=(Autodesk.Revit.DB.Plumbing.Pipe)doc.GetElement(_ledgerPipe!);
                    var second=NativePipe(doc,new XYZ(200,5,3),new XYZ(205,5,3));_ledgerSecond=second.Id;
                    using(var fitting=new SubTransaction(doc)){
                        fitting.Start();try{
                            var a=first.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c=>c.Origin.DistanceTo(new XYZ(200,5,3))).First();
                            var b=second.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c=>c.Origin.DistanceTo(new XYZ(200,5,3))).First();
                            doc.Create.NewElbowFitting(a,b);fitting.Commit();
                        }catch(Exception ex){fitting.RollBack();File.WriteAllText(Path.Combine(_folder,"phase4-fitting-unavailable.txt"),ex.Message);}
                    }
                    tx.Commit();
                }
                Next(403);return;
            }
            if(_step==403){
                _ledgerCount=LiveSessionLedger.LiveIds(doc).Count;_previousLive=service.LastStatistics;
                ClashRadarPanel.Instance.SetScanStatus("Verification re-check",false);
                LiveMonitorService.Instance.RequestScan(LiveScanAction.Ledger);LiveMonitorService.Instance.ExecuteQueued(app);Next(404);return;
            }
            if(_step==404){
                var stats=service.LastStatistics!;
                Check(!ReferenceEquals(stats,_previousLive)&&stats.Sources==_ledgerCount+1&&stats.IndexedElements==0&&stats.Scope.CoversHost(_ledgerBaseline!.Value)&&LiveSessionLedger.LiveIds(doc).All(id=>stats.Scope.CoversHost(id.Value)),"Re-check covers the live ledger plus its known baseline clash partner and indexes zero elements");
                Check(!ScanCoordinator.Busy,"Ledger re-check does not start a full-model coordinator job");
                Check(Descendants<Button>(ClashRadarPanel.Instance).Any(b=>(b.Content as string)=="Re-check session"&&b.IsEnabled),"Live completion re-enables the view-model recheck command");
                Check(!ScanCoordinator.ResultsStale&&!LiveSessionLedger.Store.HasUnchecked(DocumentSession.Key(doc)),"Live work is current after local checks finish with no unchecked ledger IDs");
                File.WriteAllText(Path.Combine(_folder,"phase4-ledger-scan.json"),JsonConvert.SerializeObject(new {ledgerCount=_ledgerCount,statistics=stats,placementEvents=PlacementEvents,addedFittings=AddedFittings},Formatting.Indented));
                CaptureLifecycleUi(ClashRadarPanel.Instance,"phase4-radar.png");
                File.WriteAllText(Path.Combine(_folder,"phase4-awaiting-undo.txt"),"Use Revit Undo once: Phase4 place segment 2. The public PostCommand API does not permit Undo.");Next(405);return;
            }
            if(_step==405){
                if(doc.GetElement(_ledgerSecond!)!=null){if(DateTime.UtcNow-_next>TimeSpan.FromMinutes(5))throw new InvalidOperationException("Waiting for UI Undo.");return;}
                Check(doc.GetElement(_ledgerSecond!)==null&&!LiveSessionLedger.LiveIds(doc).Any(id=>id==_ledgerSecond),"Undo removes the placed segment from the live ledger");
                File.WriteAllText(Path.Combine(_folder,"phase4-awaiting-redo.txt"),"Use Revit Redo once: Phase4 place segment 2.");Next(406);return;
            }
            if(_step==406){
                if(doc.GetElement(_ledgerSecond!)==null){if(DateTime.UtcNow-_next>TimeSpan.FromMinutes(5))throw new InvalidOperationException("Waiting for UI Redo.");return;}
                Check(doc.GetElement(_ledgerSecond!)!=null&&LiveSessionLedger.LiveIds(doc).Any(id=>id==_ledgerSecond),"Redo restores the segment to the live ledger through DocumentChanged");
                Check(PlacementEvents.Count==2,"Separate placed pipe segments each produce their own DocumentChanged transaction");
                Check(AddedFittings.Count>0||File.Exists(Path.Combine(_folder,"phase4-fitting-unavailable.txt")),"Fitting Added-event observation or unavailable routing reason is recorded");
                service.ClearSession(doc);service.ExecuteQueued(app);
                var settings=AppSettings.Load().ScanSnapshot();settings.TrackEditedElements=false;AppSettings.Save(settings);
                using(var tx=new Transaction(doc,"Phase4 edited tracking off")){tx.Start();ElementTransformUtils.MoveElement(doc,_ledgerPipe!,new XYZ(0,0,2));tx.Commit();}
                Next(407);return;
            }
            if(_step==407){
                Check(!LiveSessionLedger.LiveIds(doc).Any(id=>id==_ledgerPipe),"Edited-only elements stay outside the ledger when the setting is off");
                var settings=AppSettings.Load().ScanSnapshot();settings.TrackEditedElements=true;AppSettings.Save(settings);
                using(var tx=new Transaction(doc,"Phase4 edited tracking on")){tx.Start();ElementTransformUtils.MoveElement(doc,_ledgerPipe!,new XYZ(0,0,-2));tx.Commit();}
                Next(408);return;
            }
            if(_step==408){
                Check(LiveSessionLedger.LiveIds(doc).Any(id=>id==_ledgerPipe),"Edited elements enter the ledger when the setting is on");
                using(var tx=new Transaction(doc,"Phase4 delete ledger element")){tx.Start();doc.Delete(_ledgerPipe!);tx.Commit();}
                Next(409);return;
            }
            if(_step==409){
                Check(!LiveSessionLedger.LiveIds(doc).Any(id=>id==_ledgerPipe)&&!RadarDataStore.Instance.GetActive().Any(c=>c.InvolvesHost(new HashSet<long>{_ledgerPipe!.Value})),"Deleting a ledger element prunes its entry and clears its active hard results");
                Check(!ScanCoordinator.ResultsStale,"Live deletion checks leave no unchecked live work");
                Check(ScanCoordinator.FullResultsStale,"Live deletion does not certify the separate Dashboard snapshot");
                service.Stop();Check(LiveSessionLedger.Count==0,"Stopping Live Monitor clears the live ledger");
                Check(!ClashRadarPanel.Instance.ViewModel.CheckChangesCommand.CanExecute(null)&&ClashRadarPanel.Instance.ViewModel.StatusText=="Off","Stopped monitor disables the check command and displays Off");
                var closing=app.Application.NewProjectDocument(UnitSystem.Metric);string closedKey=DocumentSession.Key(closing);
                LiveSessionLedger.Store.Note(closedKey,1,"fixture",LedgerOrigin.Drawn);closing.Close(false);DocumentSession.PruneClosedDocuments();
                Check(LiveSessionLedger.Store.Entries(closedKey).Count==0,"Closing a document clears its ledger even with the monitor stopped");
                app.Application.DocumentChanged-=RecordLedgerEvent;
                ScanCoordinator.Start(doc,"",null,(rows,stats)=>ClashDashboard.Instance.MergeFullScan(rows,stats.Mode,stats.Scope));
                Next(410);return;
            }
            if(_step==410){
                var stale=ClashDashboard.Instance.GetVisibleClashes().Where(ScanCoordinator.IsStale).ToList();
                File.WriteAllText(Path.Combine(_folder,"phase6-full-readiness.json"),JsonConvert.SerializeObject(new {fullStale=ScanCoordinator.FullResultsStale,liveStale=ScanCoordinator.ResultsStale,inputsStale=ScanCoordinator.InputsStale,staleRows=stale.Select(c=>new {c.ClashId,c.ElementAId,c.ElementBId,c.LinkInstanceA,c.LinkInstanceB,c.Status,c.GeometryRevision,required=ScanCoordinator.RequiredRevision(c)})},Formatting.Indented));
                Check(!ScanCoordinator.FullResultsStale,"Complete Full Scan clears pending target and restored dependant edits");
                Check(stale.Count==0,"Explicit Full Scan refreshes Dashboard and resolves its deleted-element rows");
                _step=500;_next=DateTime.UtcNow.AddSeconds(2);
            }
        }
    }
}
