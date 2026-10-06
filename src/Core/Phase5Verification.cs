using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Dashboard;
using ClashResolveAI.LiveMonitor;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;

namespace ClashResolveAI.Core
{
    internal static partial class IntegrationVerification
    {
        private static ElementId? _phase5Pipe,_phase5Type;
        private static string _phase5Full="",_phase5Radar="";
        private static string Signature(IEnumerable<ClashResult> rows)=>string.Join("|",rows.OrderBy(c=>c.ClashId).Select(c=>c.ClashId+":"+c.Status+":"+c.Origin+":"+c.GeometryRevision+":"+c.LiveSessionId));
        private static string Signature(IEnumerable<LiveClashDto> rows)=>string.Join("|",rows.OrderBy(c=>c.ClashId).Select(c=>c.ClashId+":"+c.Status+":"+c.Origin+":"+c.GeometryRevision+":"+c.LiveSessionId));
        private static void Phase5Step(UIApplication app)
        {
            var doc=_test!;var service=LiveMonitorService.Instance;var radar=RadarDataStore.Instance;var dashboard=ClashDashboard.Instance;
            void Next(int step){_step=step;_next=DateTime.UtcNow.AddSeconds(2);}
            void Full()=>ScanCoordinator.Start(doc,"",null,(rows,stats)=>dashboard.MergeFullScan(rows,stats.Mode,stats.Scope));
            if(service.HasPendingWork||ScanCoordinator.Busy){if(DateTime.UtcNow-_next>TimeSpan.FromMinutes(3))throw new InvalidOperationException("Phase 5 timed out at "+_step);return;}
            if(_step==500){
                using(var tx=new Transaction(doc,"Phase5 baseline")){
                    tx.Start();var a=NativePipe(doc,new XYZ(300,-5,3),new XYZ(300,5,3));var b=NativePipe(doc,new XYZ(295,0,3),new XYZ(305,0,3));
                    _phase5Pipe=a.Id;_phase5Type=((ElementType)doc.GetElement(a.GetTypeId())).Duplicate("Phase5 bounded type").Id;
                    a.ChangeTypeId(_phase5Type);b.ChangeTypeId(_phase5Type);tx.Commit();
                }
                var family=app.Application.NewFamilyDocument(@"C:\ProgramData\Autodesk\RVT 2024\Family Templates\English\Metric Generic Model.rft");
                try {
                    using(var tx=new Transaction(family,"Phase5 family fixture")){
                        tx.Start();family.FamilyManager.NewType("Phase5 type");
                        var loop=new CurveArray();var points=new[]{XYZ.Zero,XYZ.BasisX,XYZ.BasisX+XYZ.BasisY,XYZ.BasisY};
                        for(int i=0;i<4;i++)loop.Append(Line.CreateBound(points[i],points[(i+1)%4]));
                        var loops=new CurveArrArray();loops.Append(loop);
                        family.FamilyCreate.NewExtrusion(true,loops,SketchPlane.Create(family,Plane.CreateByNormalAndOrigin(XYZ.BasisZ,XYZ.Zero)),1);tx.Commit();
                    }
                    family.SaveAs(Path.Combine(_folder,"phase5-family.rfa"),new SaveAsOptions {OverwriteExistingFile=true});
                } finally {family.Close(false);}
                DocumentSession.Activate(doc);radar.Clear();Full();Next(501);return;
            }
            if(_step==501){
                Check(dashboard.Clashes.Any(c=>c.InvolvesHost(new HashSet<long>{_phase5Pipe!.Value}))&&radar.ActiveCount==0,"Full Scan populates only Dashboard");
                _phase5Full=Signature(dashboard.Clashes);service.Start(app);service.ClearSession(doc);service.ExecuteQueued(app);
                app.ActiveUIDocument.Selection.SetElementIds(new[]{_phase5Pipe!});service.CheckCurrentSelection();Next(502);return;
            }
            if(_step==502){
                Check(Signature(dashboard.Clashes)==_phase5Full,"Live selection scan leaves Full Scan results and revisions unchanged");
                Check(radar.GetActive().Count>0&&radar.GetActive().All(c=>c.Origin==ResultOrigin.Live&&c.LiveSessionId==radar.SessionId),"Live results carry Live origin and current session");
                var live=radar.GetActive().First();var full=dashboard.Clashes.Single(c=>c.ClashId==live.ClashId);var before=full.Status;
                radar.IgnoreClash(live);Check(full.Status==before,"Live Ignore leaves identical Full pair lifecycle unchanged");
                radar.Types=ResultTypes.All;_phase5Radar=Signature(radar.GetActive());Full();Next(503);return;
            }
            if(_step==503){
                Check(Signature(radar.GetActive())==_phase5Radar&&radar.Types==ResultTypes.All,"Subsequent Full Scan leaves Radar results and filters unchanged");
                Check(!Descendants<Button>(ClashRadarPanel.Instance).Any(b=>(b.Content as string)?.Contains("Full rescan")==true),"Radar has no Full Scan action");
                _previousLive=service.LastStatistics;app.ActiveUIDocument.Selection.SetElementIds(new ElementId[0]);
                using(var tx=new Transaction(doc,"Phase5 level edit")){tx.Start();new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElement().Name="Phase5 input level";tx.Commit();}
                Next(504);return;
            }
            if(_step==504){
                Check(ScanCoordinator.InputsStale&&ReferenceEquals(service.LastStatistics,_previousLive)&&!ScanCoordinator.Busy,"Level input edit marks stale without any live or global job");
                using(var tx=new Transaction(doc,"Phase5 pipe type edit")){tx.Start();doc.GetElement(_phase5Type!).Name="Phase5 renamed bounded type";tx.Commit();}
                Next(505);return;
            }
            if(_step==505){
                var stats=service.LastStatistics!;
                Check(ReferenceEquals(stats,_previousLive)&&service.ScanStatus?.RequiresFullScan==true&&!ScanCoordinator.Busy,"Type edit requires Full Scan and preserves accumulated changes without starting a partial job");
                Check(ScanCoordinator.InputsStale,"Local type checks leave model inputs stale until Full Scan");
                DrainUi(ClashRadarPanel.Instance);
                Check(Descendants<TextBlock>(ClashRadarPanel.Instance).Any(t=>t.Text=="Model inputs changed, run Full Scan to re-verify"&&t.IsVisible),"Persistent nonblocking model-input banner is visible");
                CaptureLifecycleUi(ClashRadarPanel.Instance,"phase5-radar-banner.png");
                File.WriteAllText(Path.Combine(_folder,"phase5-type-scan.json"),JsonConvert.SerializeObject(stats,Formatting.Indented));
                _previousLive=stats;
                using(var tx=new Transaction(doc,"Phase5 load family")){tx.Start();Check(doc.LoadFamily(Path.Combine(_folder,"phase5-family.rfa"),out var loaded)&&loaded!=null,"Real fixture family loads successfully");tx.Commit();}
                Next(506);return;
            }
            if(_step==506){
                Check(ScanCoordinator.InputsStale&&ReferenceEquals(service.LastStatistics,_previousLive)&&!ScanCoordinator.Busy,"Family load starts no global or empty local job and retains input banner");
                Full();Next(507);return;
            }
            if(_step==507){
                Check(!ScanCoordinator.InputsStale,"Explicit completed Full Scan clears the input banner");
                service.Stop();
                using(var tx=new Transaction(doc,"Phase5 cap fixture")){
                    tx.Start();_phase5Type=((ElementType)doc.GetElement(_phase5Type!)).Duplicate("Phase5 over cap").Id;
                    for(int i=0;i<501;i++)NativePipe(doc,new XYZ(600+i*2,0,3),new XYZ(600+i*2,1,3)).ChangeTypeId(_phase5Type);
                    tx.Commit();
                }
                service.Start(app);service.ClearSession(doc);service.ExecuteQueued(app);_previousLive=service.LastStatistics;
                using(var tx=new Transaction(doc,"Phase5 over cap type edit")){tx.Start();doc.GetElement(_phase5Type!).Name="Phase5 over cap edited";tx.Commit();}
                Next(508);return;
            }
            if(_step==508){
                Check(ReferenceEquals(service.LastStatistics,_previousLive)&&!service.HasPendingWork&&!ScanCoordinator.Busy&&ScanCoordinator.InputsStale,"501-instance type edit shows input banner without partial or global scan");
                radar.BeginSession(true);
                Check(radar.GetVisible().Count==0,"New live session hides old live results");radar.ThisSession=false;
                Check(radar.GetVisible().All(c=>c.Origin==ResultOrigin.Live),"All-session Radar filter still excludes Full results");
                service.Stop();Full();Next(509);return;
            }
            if(_step==509){
                Check(!ScanCoordinator.FullResultsStale,"Full Scan certifies Dashboard after cap fixture changes");
                _phase5Full=Signature(dashboard.Clashes);service.Start(app);service.ClearSession(doc);service.ExecuteQueued(app);
                using(var tx=new Transaction(doc,"Phase5 draw after Full Scan")){tx.Start();NativePipe(doc,new XYZ(295,2,3),new XYZ(305,2,3));tx.Commit();}
                Next(510);return;
            }
            if(_step==510){
                Check(radar.GetVisible().Count>0&&Signature(dashboard.Clashes)==_phase5Full,"Drawing after Full Scan updates Radar and leaves Dashboard snapshot unchanged");
                Check(ScanCoordinator.FullResultsStale&&!ScanCoordinator.ResultsStale,"Live completion cannot certify the earlier Dashboard snapshot for export");
                IEnumerable<int> Waiting(){while(true)yield return 0;}
                ScanCoordinator.StartJob(doc,new ClashResolveAI.ClashEngine.ScanJob(Waiting(),new List<ClashResult>(),new ClashResolveAI.ClashEngine.ScanStatistics()),(_,__)=>{});
                LiveMonitorService.Instance.RequestScan(LiveScanAction.Cancel);LiveMonitorService.Instance.ExecuteQueued(app);
                Check(ScanCoordinator.Busy,"Radar Cancel does not cancel an independent Full Scan");ScanCoordinator.Cancel();
                var liveRows=radar.GetActive().ToList();
                File.WriteAllText(Path.Combine(_folder,"phase5-routing.json"),JsonConvert.SerializeObject(new {dashboard=dashboard.Clashes.Select(c=>new {c.ClashId,c.Origin,c.Status}),radar=liveRows.Select(c=>new {c.ClashId,c.Origin,c.LiveSessionId,c.Status}),cap=501},Formatting.Indented));
                service.Stop();_timer?.Stop();File.WriteAllText(Path.Combine(_folder,"complete.txt"),"PASS "+Results.Count+" checks. "+(Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_PHASE0")=="1"?"Engine-only entry: native image-export sequence not executed.":"Full integration, native snapshots, Phase 4 ledger/Undo/Redo and Phase 5 routing completed."));
                ProductionVerification.Begin(app,_folder);
            }
        }
    }
}
