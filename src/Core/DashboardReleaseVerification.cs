using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Dashboard;
using ClashResolveAI.Dashboard.Application;
using ClashResolveAI.Dashboard.Persistence;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Inspection;
using Newtonsoft.Json;
namespace ClashResolveAI.Core
{
    internal static partial class IntegrationVerification
    {
        private static string _dashboardFirst="",_dashboardFailure="",_dashboardSnapshot="";
        private static ElementId? _dashboardLinkType,_dashboardLevel;
        private static DashboardWindow? _dashboardWindow;
        private static InspectionScene? _dashboardScene;
        private static string _dashboardBeforeFailure="",_dashboardOriginalKey="";
        private static void DashboardScan(int next,ScanMode mode=ScanMode.HardOnly,string level="")
        {
            _dashboardFailure="";
            ScanCoordinator.StartJob(_test!,new ClashResolveAI.ClashEngine.ClashEngine(_test!).CreateJob(null,true,null,level,mode,false),(_,__)=>{},failed:error=>_dashboardFailure=error,completeScope:level=="");
            _step=next;_next=DateTime.UtcNow.AddSeconds(1);
        }
        private static void DashboardNext(int next){_step=next;_next=DateTime.UtcNow.AddSeconds(1);}
        private static void DashboardReleaseStep(UIApplication app)
        {
            if(ScanCoordinator.Busy){_next=DateTime.UtcNow.AddSeconds(1);return;}
            var db=ClashDatabase.Instance;var dashboard=ClashDashboard.Instance;
            if(_step==0){
                Check(app.ActiveUIDocument==null,"Dashboard release verification starts in a fresh disposable Revit process");_step=-99;
                var link=app.Application.NewProjectDocument(UnitSystem.Metric);using(var tx=new Transaction(link,"Dashboard linked fixture")){tx.Start();Box(link,BuiltInCategory.OST_StructuralColumns,XYZ.Zero);tx.Commit();}
                string linkPath=Path.Combine(_folder,"dashboard-link.rvt");link.SaveAs(linkPath);link.Close(false);
                var host=app.Application.NewProjectDocument(UnitSystem.Metric);using(var tx=new Transaction(host,"Dashboard host fixture")){tx.Start();_moving=Box(host,BuiltInCategory.OST_MechanicalEquipment,new XYZ(.5,.5,0)).Id;Box(host,BuiltInCategory.OST_StructuralColumns,XYZ.Zero);var lower=Level.Create(host,0);_dashboardLevel=Level.Create(host,100).Id;var type=new FilteredElementCollector(host).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.FloorPlan);ViewPlan.Create(host,type.Id,lower.Id);var loaded=RevitLinkType.Create(host,ModelPathUtils.ConvertUserVisiblePathToModelPath(linkPath),new RevitLinkOptions(false));_dashboardLinkType=loaded.ElementId;RevitLinkInstance.Create(host,loaded.ElementId);var repeated=RevitLinkInstance.Create(host,loaded.ElementId);ElementTransformUtils.RotateElement(host,repeated.Id,Line.CreateBound(new XYZ(1,1,0),new XYZ(1,1,10)),Math.PI/6);tx.Commit();}
                string path=Path.Combine(_folder,"dashboard-host.rvt");host.SaveAs(path);host.Close(false);_test=app.OpenAndActivateDocument(path).Document;DocumentSession.Activate(_test);Commands.DashboardCommand.EnsureApiActions();DashboardScan(901);return;
            }
            if(_step==901){
                Check(_dashboardFailure==""&&dashboard.Clashes.Count>=3,"Full Scan publishes distinct host and repeated transformed-link findings");_dashboardFirst=db.History.GetVersions().Last(v=>v.State==ScanVersionState.Completed).ScanId;_dashboardSnapshot=JsonConvert.SerializeObject(db.History.GetObservations(_dashboardFirst));
                var group=db.History.GetGroups(_dashboardFirst).First();new GroupCoordinationService(db).Assign(_dashboardFirst,group.GroupKey,"Native coordinator",DateTime.UtcNow.Date.AddDays(7),"verification");
                Check(db.ReadGroupState(group.GroupKey).Owner=="Native coordinator","Native group default assignment persists");
                var row=dashboard.Clashes.First();var handler=new DashboardPreviewHandler {Pending=row,Completed=(scene,error)=>{_dashboardScene=scene;Check(scene!=null,"Panel preview handler copies actual host/link geometry: "+error);}};handler.Execute(app);
                Check(_dashboardScene!.Meshes.Any(m=>m.Role==0)&&_dashboardScene.Meshes.Any(m=>m.Role==1)&&_dashboardScene.HasOverlap,"Preview scene contains both components and real overlap");
                dashboard.ShowWindow();_dashboardWindow=dashboard.OpenWindow;_dashboardWindow!.SelectIssues();_dashboardWindow.UpdateLayout();Descendants<System.Windows.Controls.DataGrid>(_dashboardWindow).Single(g=>g.Columns.Any(c=>Equals(c.Header,"GroupId"))).SelectedIndex=0;
                _navigationTarget=row;_requestedView=Services.ClashViewNavigation.Show(app,row,false);DashboardNext(902);return;
            }
            if(_step==902){Check(app.ActiveUIDocument.ActiveView is ViewPlan&&app.ActiveUIDocument.ActiveView.Id.Value==_requestedView,"Dashboard 2D navigation opens a native plan");Check(Descendants<System.Windows.Controls.TextBlock>(_dashboardWindow!).Any(t=>t.Text.Contains("Red: current solid intersection")),"Embedded panel automatically renders actual copied 2D/3D clash geometry");CaptureLifecycleUi(_dashboardWindow!,"dashboard-native-panel.png");_requestedView=Services.ClashViewNavigation.Show(app,_navigationTarget!,true);DashboardNext(903);return;}
            if(_step==903){Check(app.ActiveUIDocument.ActiveView is View3D&&app.ActiveUIDocument.ActiveView.Id.Value==_requestedView,"Dashboard 3D navigation opens a native clash view");using(var tx=new Transaction(_test!,"Clear fixture clash")){tx.Start();ElementTransformUtils.MoveElement(_test!,_moving!,new XYZ(5,0,0));tx.Commit();}DashboardScan(904);return;}
            if(_step==904){Check(_dashboardFailure==""&&dashboard.Clashes.All(c=>c.Status==ClashStatus.Resolved),"V002 reliably resolves absent host/link clashes");DashboardScan(905);return;}
            if(_step==905){var latest=db.History.GetVersions().Last(v=>v.State==ScanVersionState.Completed);Check(db.History.GetObservations(latest.ScanId).All(r=>r.ChangeKind==ClashChangeKind.UnchangedResolved),"V003 repeated absence does not inflate resolved deltas");using(var tx=new Transaction(_test!,"Restore fixture clash")){tx.Start();ElementTransformUtils.MoveElement(_test!,_moving!,new XYZ(-5,0,0));tx.Commit();}DashboardScan(906);return;}
            if(_step==906){Check(_dashboardFailure==""&&dashboard.Clashes.All(c=>c.Status==ClashStatus.Reopened),"V004 confirmed recurrence reopens the same identities");Check(JsonConvert.SerializeObject(db.History.GetObservations(_dashboardFirst))==_dashboardSnapshot,"Historical geometry labels/workflow remain unchanged after native recurrence");
                var source=new DashboardDataSource(db,()=>DocumentSession.CurrentKey,id=>false);var frozen=source.Read("snapshot:"+_dashboardFirst,"");var request=DashboardExportService.Capture(frozen,frozen.Rows,null,DashboardExportFormat.Bcf,"All captured findings");File.WriteAllText(Path.Combine(_folder,"historical-export-path.txt"),DashboardExportService.Write(request,_folder));
                request.Format=DashboardExportFormat.Excel;Check(File.Exists(DashboardExportService.Write(request,_folder)),"Native historical Excel export writes captured rows");request.Format=DashboardExportFormat.Word;Check(File.Exists(DashboardExportService.Write(request,_folder)),"Native historical Word export writes captured rows");
                _dashboardWindow!.SelectAnalytics();_dashboardWindow.UpdateLayout();CaptureLifecycleUi(_dashboardWindow,"dashboard-native-analytics.png");Check(true,"Native Analytics workspace renders captured scan history");
                DashboardScan(907,ScanMode.HardOnly,_dashboardLevel!.Value.ToString());return;
            }
            if(_step==907){var latest=db.History.GetVersions().Last(v=>v.State==ScanVersionState.Completed);Check(db.History.GetObservations(latest.ScanId).All(r=>r.ChangeKind==ClashChangeKind.NotEvaluated),"Empty upper-level scope does not resolve lower-level findings");DashboardScan(908,ScanMode.HardAndClearance);return;}
            if(_step==908){var latest=db.History.GetVersions().Last(v=>v.State==ScanVersionState.Completed);var previous=db.History.GetVersions().Where(v=>v.State==ScanVersionState.Completed&&v.SequenceNumber<latest.SequenceNumber).Last();var compare=new ScanComparisonService().Compare(new ScanSnapshot {Version=previous,Issues=db.History.GetObservations(previous.ScanId)},new ScanSnapshot {Version=latest,Issues=db.History.GetObservations(latest.ScanId)});Check(compare.Issues.Any(i=>(i.Flags&ClashChangeFlags.ConfigurationChanged)!=0),"Changed scan mode is exposed as configuration change");
                ((RevitLinkType)_test!.GetElement(_dashboardLinkType!)).Unload(null);DashboardScan(909,ScanMode.HardAndClearance);return;}
            if(_step==909){var latest=db.History.GetVersions().Last(v=>v.State==ScanVersionState.Completed);Check(db.History.GetObservations(latest.ScanId).Where(r=>r.LinkInstanceA!=""||r.LinkInstanceB!="").All(r=>r.ChangeKind==ClashChangeKind.NotEvaluated),"Unloaded links retain neutral historical findings rather than false resolution");
                var frozen=new DashboardDataSource(db,()=>DocumentSession.CurrentKey,id=>false).Read("snapshot:"+_dashboardFirst,"");new DashboardHistoryNavigationHandler {Pending=frozen.Rows.First(r=>r.LinkInstanceA==""&&r.LinkInstanceB=="")}.Execute(app);Check(app.ActiveUIDocument.Selection.GetElementIds().Count==2,"Historical navigation selects available current components by stable identity");
                var count=db.History.GetVersions().Count(v=>v.State==ScanVersionState.Completed);IEnumerable<int> Waiting(){while(true)yield return 0;}ScanCoordinator.StartJob(_test!,new ClashResolveAI.ClashEngine.ScanJob(Waiting(),new List<ClashResult>(),new ClashResolveAI.ClashEngine.ScanStatistics()),(_,__)=>{});ScanCoordinator.Cancel("Dashboard release cancellation fixture");Check(db.History.GetVersions().Last().State==ScanVersionState.Cancelled&&db.History.GetVersions().Count(v=>v.State==ScanVersionState.Completed)==count,"Cancelled native scan retains previous completed history");
                var key=DocumentSession.CurrentKey;var before=db.History.GetVersions().Count;db.Dispose();db.Open(key);Check(db.History.GetVersions().Count==before&&db.History.GetObservations(_dashboardFirst).Count>=3,"Native database restart retains scan observations and attempts");
                _dashboardBeforeFailure=JsonConvert.SerializeObject(db.History.GetCurrent());_dashboardOriginalKey=key;
                using(var con=new System.Data.SQLite.SQLiteConnection("Data Source="+Path.Combine(_folder,"databases",key+".clash.db"))){con.Open();using var command=con.CreateCommand();command.CommandText="CREATE TRIGGER dashboard_save_failure BEFORE INSERT ON ClashScanRevisions BEGIN SELECT RAISE(ABORT,'injected dashboard save failure'); END";command.ExecuteNonQuery();}
                DashboardScan(910,ScanMode.HardAndClearance);return;
            }
            if(_step==910){Check(_dashboardFailure!=""&&db.History.GetVersions().Last().State==ScanVersionState.Failed,"Native save failure is retained as a failed attempt");Check(JsonConvert.SerializeObject(db.History.GetCurrent())==_dashboardBeforeFailure,"Native failed save rolls back current state without history loss");using(var con=new System.Data.SQLite.SQLiteConnection("Data Source="+Path.Combine(_folder,"databases",_dashboardOriginalKey+".clash.db"))){con.Open();using var command=con.CreateCommand();command.CommandText="DROP TRIGGER dashboard_save_failure";command.ExecuteNonQuery();}((RevitLinkType)_test!.GetElement(_dashboardLinkType!)).Load();DashboardScan(911,ScanMode.HardAndClearance);return;}
            if(_step==911){Check(_dashboardFailure==""&&dashboard.Clashes.Any(c=>(c.LinkInstanceA!=""||c.LinkInstanceB!="")&&c.Status!=ClashStatus.Resolved),"Reloaded linked model restores current linked evidence without false resolution");using(var tx=new Transaction(_test!,"Delete clash component")){tx.Start();_test!.Delete(_moving!);tx.Commit();}DashboardScan(912,ScanMode.HardAndClearance);return;}
            if(_step==912){Check(_dashboardFailure=="","Deleting a source component still allows a complete saved scan");var prior=db.History.GetObservations(_dashboardFirst).First();bool explained=false;InspectionHandler.Run(app,ClashObservationAdapter.Restore(prior),null,null,(scene,error)=>explained=scene==null&&error.Contains("unavailable"));Check(explained,"Deleted historical component explains why current preview is unavailable");
                string savedAs=Path.Combine(_folder,"dashboard-save-as.rvt");_test!.SaveAs(savedAs);var other=app.Application.NewProjectDocument(UnitSystem.Metric);string otherPath=Path.Combine(_folder,"dashboard-other.rvt");other.SaveAs(otherPath);other.Close(false);app.OpenAndActivateDocument(otherPath);_test.Close(false);_test=app.OpenAndActivateDocument(savedAs).Document;DocumentSession.Activate(_test);Check(DocumentSession.CurrentKey!=_dashboardOriginalKey&&db.History.GetVersions().Count==0,"Save As on reopen starts an isolated document lineage");Check(dashboard.Clashes.Count==0,"Document switch clears current dashboard identities");
                _dashboardWindow?.Close();_timer?.Stop();File.WriteAllText(Path.Combine(_folder,"complete.txt"),"PASS "+Results.Count+" dashboard native checks. Panel previews, navigation, exports, recurrence, partial scope, changed settings, unloaded/reloaded links, cancellation, failed-save rollback, deletion, restart, document switching and Save As verified. Representative production performance and full live runtime acceptance remain separately scoped.");return;}
        }
    }
}
