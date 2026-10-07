using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClashResolveAI.Core;
using ClashResolveAI.ClashEngine;
using ClashResolveAI.Dashboard;
using ClashResolveAI.Dashboard.Application;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Dashboard.Persistence;
using Newtonsoft.Json;
internal static partial class Program
{
    private static void Phase45(string root)
    {
        var run=Path.Combine(root,"phase45-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(run);Environment.SetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR",run);
        using var db=new ClashDatabase();db.Open("fixture");
        ClashResult Row(string id,long a){var r=Issue(id,a);r.SystemTypeA="Supply";r.Metadata=new ClashMetadata();return r;}
        var a=Row("group-a",1);var b=Row("group-b",3);
        FullScanSnapshotBatch Scan(params ClashResult[] rows){var v=db.History.BeginScan(Capture());var stats=new ScanStatistics {ScanId=v.ScanId,Mode=ScanMode.HardOnly,Scope=new ScanScope("fixture")};foreach(var current in db.LoadCurrentClashes().Concat(rows))stats.Scope.Note(current.ElementAId,"","");return new FullScanDashboardService(db).Complete(db.LoadCurrentClashes(),rows,stats,9);}
        Scan(a,b);var v1=db.History.GetVersions().Last();var key=db.History.GetGroups(v1.ScanId).Single().GroupKey;
        var commands=new GroupCoordinationService(db);commands.Assign(v1.ScanId,key,"Route team",DateTime.UtcNow.Date.AddDays(5),"tester");
        Check(db.LoadCurrentClashes().All(r=>r.Metadata.AssignedEngineer=="Route team"),"Group defaults assign all non-overridden members");
        Check(db.History.GetEvents("group-a").Last().Origin=="GroupAssignment"&&db.History.GetEvents("group-b").Last().Origin=="GroupAssignment","Group assignment audits every changed issue");
        new LifecycleCommandService(db).UpdateMetadata(new[]{"group-a"},"Manual owner",DateTime.UtcNow.Date.AddDays(2),"Override","tester");
        commands.Assign(v1.ScanId,key,"Updated route team",DateTime.UtcNow.Date.AddDays(7),"tester");
        Check(db.History.FindCurrent("group-a")!.MetadataJson.Contains("Manual owner")&&db.History.FindCurrent("group-b")!.MetadataJson.Contains("Updated route team"),"Issue overrides survive later group defaults");
        Scan(Row("group-a",1),Row("group-b",3));var v2=db.History.GetVersions().Last();
        Check(db.History.GetGroups(v2.ScanId).Single().GroupKey==key&&db.ReadGroupState(key).Owner=="Updated route team","Group identity/defaults survive unchanged rescan");
        Check(db.History.FindCurrent("group-a")!.MetadataJson.Contains("Manual owner"),"Manual assignment survives rescan inheritance");
        var c=Row("group-c",4);Scan(Row("group-a",1),Row("group-b",3),c);var v3=db.History.GetVersions().Last();var newKey=db.History.GetGroups(v3.ScanId).Single().GroupKey;
        Check(newKey!=key&&db.History.GetLineage(v3.ScanId).Single().Kind=="MembershipChanged","Changed membership has durable deterministic lineage");
        Check(db.ReadGroupState(newKey).Owner=="Updated route team"&&db.History.FindCurrent("group-c")!.MetadataJson.Contains("Updated route team"),"New members inherit coordination defaults across membership change");
        Reject(()=>commands.Assign(v1.ScanId,key,"Wrong",null,"tester"),"Historical group commands reject stale membership");
        var dbPath=Path.Combine(run,"databases","fixture.clash.db");
        using(var con=new SQLiteConnection("Data Source="+dbPath)){con.Open();Sql(con,"CREATE TRIGGER fail_group BEFORE INSERT ON GroupCoordinationEvents WHEN NEW.Author='fail' BEGIN SELECT RAISE(ABORT,'injected group failure'); END;");}
        Reject(()=>commands.Assign(v3.ScanId,newKey,"Must roll back",null,"fail"),"Late group audit failure rolls back defaults and member changes");
        Check(db.ReadGroupState(newKey).Owner=="Updated route team"&&!db.History.FindCurrent("group-b")!.MetadataJson.Contains("Must roll back"),"Group/default/member state remains atomic after failure");
        using(var con=new SQLiteConnection("Data Source="+dbPath)){con.Open();Sql(con,"DROP TRIGGER fail_group");Reject(()=>Sql(con,"DELETE FROM GroupLineage"),"Group lineage is immutable");Reject(()=>Sql(con,"UPDATE GroupCoordinationEvents SET Author='tampered'"),"Group coordination audit is immutable");}
        commands.Status(v3.ScanId,newKey,ClashStatus.OnSite,"tester","");Check(db.LoadCurrentClashes().All(r=>r.Status==ClashStatus.OnSite),"Group status uses validated per-issue bulk lifecycle");
        commands.Status(v3.ScanId,newKey,ClashStatus.Resolved,"tester","Fixed route");Check(db.History.GetEvents("group-c").Last().ToStatus=="Resolved","Group bulk status audits every member");
        var source=new DashboardDataSource(db,()=>"fixture",id=>false);var vm=new DashboardWorkspace(source,new LifecycleCommandService(db),commands);vm.Refresh();
        Check(vm.GroupViews().Any(g=>g.Key==key)&&vm.GroupViews().Any(g=>g.Key==newKey),"Comparison groups include both prior and current memberships");
        var offenderRevision=db.History.GetGroups(v3.ScanId).Single();offenderRevision.PrimaryOffender="Shared component";
        var offenderView=GroupCoordinationService.Project(new DashboardSnapshot {Groups=new List<GroupScanRevision>{offenderRevision},Rows=db.History.GetObservations(v3.ScanId).ToList()}).Single();
        Check(offenderView.PrimaryOffender?.ElementId==2&&offenderView.PrimaryOffender.ElementUniqueId=="unique-b"&&offenderView.PrimaryOffender.DocumentKey=="fixture","Group offender exposes structured document/link/element identity");
        var exportGroup=vm.GroupViews().Single(g=>g.Key==newKey);var projection=DashboardExportProjection.GroupTopic(exportGroup);string archive=new ClashResolveAI.Services.BcfExportService().ExportGroups(new List<ClashGroup>{projection},"Phase45 group",root);
        var topic=new ClashResolveAI.Services.BcfExportService().ImportBcf(archive).Single();Check(topic.TopicStatus=="Resolved"&&topic.AssignedTo=="Updated route team"&&topic.Description.Contains("Manual owner")&&topic.Description.Contains("Override"),"Group BCF preserves group default owner, workflow distribution and member overrides/comments");File.WriteAllText(Path.Combine(root,"group-export-path.txt"),archive);
        vm.ScanId="snapshot:"+v3.ScanId;vm.Refresh();Check(!vm.Snapshot.IsCurrent&&vm.Snapshot.Rows.All(r=>r.Status=="InReview"),"Latest version can be browsed as an immutable capture rather than current workflow");
        vm.ScanId=v2.ScanId;vm.Refresh();Check(vm.GroupViews().Single().Members.All(r=>r.Status=="InReview"),"Historical group workflow remains capture-time state");
        Reject(()=>vm.AssignGroup(key,"No",null),"Historical group editing blocked at workspace boundary");
        Check(source.Timeline("group-a").Any(e=>e.Lane=="Scan observation")&&source.Timeline("group-a").Any(e=>e.Lane=="User workflow / coordination"),"Issue timeline separates observations and user workflow events");
        var failed=db.History.BeginScan(Capture());db.History.EndAttempt(failed.ScanId,ScanVersionState.Failed,"Injected save failure");var cancelled=db.History.BeginScan(Capture());db.History.EndAttempt(cancelled.ScanId,ScanVersionState.Cancelled,"Cancelled by user");vm.ScanId="";vm.Refresh();
        Check(vm.Snapshot.Attempts.Any(v=>v.State==ScanVersionState.Cancelled)&&vm.Snapshot.Attempts.Any(v=>v.State==ScanVersionState.Failed)&&vm.Snapshot.Selected!.ScanId==v3.ScanId,"History includes failed/cancelled diagnostics without replacing completed baseline");
        db.DeleteClashes(new[]{"group-c"});vm.Refresh();Check(vm.Snapshot.VersionComparison!.Issues.Count>vm.Snapshot.Comparison!.Issues.Count,"Full version comparison retains archived identities while current metric links stay scoped");
        GroupScanRevision Group(string k,params string[] ids)=>new GroupScanRevision {GroupKey=k,Reason="Route",MemberClashIds=ids.ToList()};
        var split=GroupCoordinationService.Relate(new[]{Group("p","a","b")},new[]{Group("c1","a"),Group("c2","b")});Check(split.Count==2&&split.All(l=>l.Kind=="Split"),"Split lineage deterministically records both children");
        var merge=GroupCoordinationService.Relate(new[]{Group("p1","a"),Group("p2","b")},new[]{Group("c","a","b")});Check(merge.All(l=>l.Kind=="Merge"),"Merge lineage deterministically records both parents");
        var many=Enumerable.Range(0,20000).Select(i=>Group("stable-"+i,"member-"+i)).ToList();var timer=System.Diagnostics.Stopwatch.StartNew();var unchanged=GroupCoordinationService.Relate(many,many);timer.Stop();Check(unchanged.Count==0&&timer.ElapsedMilliseconds<5000,"20,000 unchanged groups match through indexed memberships without quadratic cross-join");Console.WriteLine("20,000 group lineage matching: "+timer.ElapsedMilliseconds+" ms");
        var conflict=GroupCoordinationService.Inherit("merged",new[]{new GroupCoordinationState {Owner="A"},new GroupCoordinationState {Owner="B"}});Check(conflict.Owner==""&&conflict.Note!="","Conflicting merge defaults require review instead of arbitrary ownership");
        var window=new DashboardWindow(vm,new DashboardActions());window.Show();window.SelectGroups();window.UpdateLayout();var groupGrid=Find<DataGrid>(window).Single(g=>g.Columns.Any(col=>Equals(col.Header,"EffectiveOwners")));groupGrid.SelectedIndex=0;Capture45(window,root,"groups.png");window.SelectHistory();Capture45(window,root,"history.png");
        Check(Find<TabItem>(window).Any(t=>Equals(t.Header,"Groups")&&t.IsEnabled)&&Find<TabItem>(window).Any(t=>Equals(t.Header,"History")&&t.IsEnabled),"Groups and History views remain usable with Analytics enabled");window.Close();
        db.Dispose();db.Open("fixture");Check(db.ReadGroupState(newKey).Owner=="Updated route team"&&db.History.GetLineage(v3.ScanId).Count==1&&source.Timeline("group-a").Count>5,"Coordination, lineage and timeline survive restart");
        var oldSnapshots=JsonConvert.SerializeObject(db.History.GetObservations(v1.ScanId));db.Dispose();
        using(var con=new SQLiteConnection("Data Source="+dbPath)){con.Open();Sql(con,"DROP TABLE GroupLineage; DROP TABLE GroupCoordinationEvents; DROP TABLE GroupCoordinationState; DELETE FROM DashboardSchema WHERE Version=2; INSERT OR IGNORE INTO DashboardSchema VALUES(1,'fixture');");}
        db.Open("fixture");Check(File.Exists(db.History.MigrationBackupPath)&&db.History.GetVersions().Count==5&&JsonConvert.SerializeObject(db.History.GetObservations(v1.ScanId))==oldSnapshots,"Schema v1 to v2 migration backs up and preserves scan history without legacy reimport");
        db.Dispose();db.Open("fixture");Check(db.History.GetVersions().Count==5,"Schema v2 migration is idempotent");
    }
    private static void Capture45(Window window,string root,string name){window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,new Action(()=>{}));window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(root,name));encoder.Save(stream);}
}
