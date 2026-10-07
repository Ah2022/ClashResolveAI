using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClashResolveAI.Core;
using ClashResolveAI.Dashboard;
using ClashResolveAI.Dashboard.Application;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Inspection;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
internal static partial class Program
{
    private static void Phase67(string root)
    {
        var first=new ScanVersion {ScanId="a",SequenceNumber=1,State=ScanVersionState.Completed,EndedAtUtc=DateTime.UtcNow.AddDays(-10),Capture=Capture()};
        var last=new ScanVersion {ScanId="b",SequenceNumber=2,State=ScanVersionState.Completed,EndedAtUtc=DateTime.UtcNow.AddDays(-1),Capture=Capture()};
        ClashObservation Row(string id,string status="InReview"){var r=ClashResolveAI.Dashboard.Persistence.ClashObservationAdapter.Capture(Issue(id,id=="one"?1:3));r.NormalizedKey=id;r.ElementUniqueIdA="unique-"+id;r.Status=status;r.OpenEpisodeAtUtc=first.EndedAtUtc;r.SeenInScanCount=3;r.ChangeKind=ClashChangeKind.Persistent;return r;}
        var one=Row("one");var two=Row("two","Resolved");var unknown=Row("unknown");unknown.TestType="Unverified";unknown.UnverifiedReason="MissingGeometry";unknown.TimestampKind="LegacyUnknownTimezone";unknown.OpenEpisodeAtUtc=null;
        var comparison=new ScanComparisonService().Compare(new ScanSnapshot {Version=first,Issues=new[]{Row("one"),Row("two"),Row("unknown")}},new ScanSnapshot {Version=last,Issues=new[]{one,two,unknown}});
        var snapshot=new DashboardSnapshot {DocumentKey="fixture",Selected=last,IsCurrent=false,Rows=new List<ClashObservation>{one,two,unknown},Comparison=comparison,VersionComparison=comparison};
        var history=new AnalyticsHistory {Scans=new List<ScanSnapshot>{new ScanSnapshot {Version=first,Issues=new[]{Row("one"),Row("two"),Row("unknown")}},new ScanSnapshot {Version=last,Issues=snapshot.Rows}},Events=new List<ClashLifecycleEvent>{new ClashLifecycleEvent {ClashId="two",FromStatus="",ToStatus="InReview",Timestamp=first.EndedAtUtc!.Value.ToString("O")},new ClashLifecycleEvent {ClashId="two",FromStatus="InReview",ToStatus="Resolved",Timestamp=first.EndedAtUtc.Value.AddDays(4).ToString("O")},new ClashLifecycleEvent {ClashId="one",FromStatus="InReview",ToStatus="Resolved",Timestamp=DateTime.UtcNow.ToString("O")}}};
        var analytics=DashboardAnalyticsService.Calculate(snapshot,snapshot.Rows,history,DateTime.UtcNow);
        Check(analytics.Charts["Open by discipline pair"].Sum(b=>b.Count)==2&&analytics.Metrics.Open==2,"Analytics open buckets reconcile with shared table policy");
        Check(analytics.Charts["Confirmed critical by level"].Sum(b=>b.Count)==1,"Analytics critical excludes uncertain geometry");
        Check(analytics.Charts["Uncertainty reasons"].Single().Label=="MissingGeometry","Analytics uncertainty uses captured reason");
        Check(analytics.Charts["Open age by status"].Any(b=>b.Label.Contains("Age unknown")),"Unknown legacy timezone does not fabricate issue age");
        Check(analytics.Resolution.Contains("1 entered Resolved")&&analytics.Resolution.Contains("4 days"),"Historical resolution rate excludes future events and measures dated lifecycle episodes");
        Check(analytics.Trend.Count==2&&analytics.Trend[0].Baseline=="Initial baseline","Trend retains explicit comparison baseline and coverage");
        Check(DashboardAnalyticsService.Calculate(snapshot,new[]{one},history,DateTime.UtcNow).Metrics.Open==1,"Analytics respects selected filter identity cohort");
        snapshot.Comparison=null;Check(DashboardAnalyticsService.Calculate(snapshot,snapshot.Rows,history,DateTime.UtcNow).Resolution.Contains("unavailable"),"Resolution rate explains insufficient baseline");snapshot.Comparison=comparison;
        var source=new AnalyticsSource {Data=snapshot,History=history};var vm=new DashboardWorkspace(source,new LifecycleCommandService(new NoCommands()));vm.Refresh();vm.DrillIds=new HashSet<string>{"one"};Check(vm.Visible().Count==1&&vm.Visible().Single().ClashId=="one","Chart drill-down uses exact issue identities");vm.DrillIds=null;
        foreach(var format in new[]{DashboardExportFormat.Bcf,DashboardExportFormat.Excel,DashboardExportFormat.Word}){
            var request=DashboardExportService.Capture(snapshot,new[]{two},null,format,"Status=Resolved");var original=two.Status;two.Status="Reopened";var file=DashboardExportService.Write(request,root);two.Status=original;
            Check(request.Rows.Single().Status=="Resolved"&&request.Scope.Contains("Historical"),format+" export freezes captured workflow and scope");
            if(format==DashboardExportFormat.Bcf){var imported=new ClashResolveAI.Services.BcfExportService().ImportBcf(file).Single();Check(imported.TopicStatus=="Resolved"&&imported.Description.Contains("Historical captured state")&&imported.Description.Contains("Baseline: a"),"Historical BCF preserves workflow and comparison metadata");File.WriteAllText(Path.Combine(root,"historical-export-path.txt"),file);}
            if(format==DashboardExportFormat.Excel){using var book=new XLWorkbook(file);Check(book.Worksheet("Issues").Cell(2,2).GetString()=="Resolved"&&book.Worksheet("Export context").Cell(3,1).GetString().Contains("Historical"),"Excel report reconciles selected rows and explicit historical context");}
            if(format==DashboardExportFormat.Word){using var doc=WordprocessingDocument.Open(file,false);var errors=new OpenXmlValidator().Validate(doc).ToList();foreach(var error in errors)Console.WriteLine("OpenXML: "+error.Description+" "+error.Path?.XPath);Check(errors.Count==0,"Word export validates against OpenXML schema");Check(doc.MainDocumentPart!.Document.InnerText.Contains("Historical captured state")&&doc.MainDocumentPart.Document.InnerText.Contains("Resolved"),"Word report includes copied workflow and scope");File.WriteAllText(Path.Combine(root,"word-export-path.txt"),file);}
        }
        var foreign=Row("other");foreign.DocumentKey="another";Reject(()=>DashboardExportService.Capture(snapshot,new[]{foreign},null,DashboardExportFormat.Excel,""),"Export rejects cross-document scope");
        var group=new GroupView {Revision=new GroupScanRevision {GroupKey="g",Title="Fixture group",Reason="Route",CoordinationJson="{}",MemberClashIds=new List<string>{"one","two"}},Members=new List<ClashObservation>{one,two},CurrentIds=new List<string>{"one","two"},Owner="Coordinator"};snapshot.Groups.Add(group.Revision);
        var groupFile=DashboardExportService.Write(DashboardExportService.Capture(snapshot,snapshot.Rows,group,DashboardExportFormat.Bcf,"Group=g"),root);var groupTopic=new ClashResolveAI.Services.BcfExportService().ImportBcf(groupFile).Single();
        Check(groupTopic.AssignedTo=="Coordinator"&&groupTopic.Description.Contains("Resolved: 1")&&groupTopic.Description.Contains("InReview: 1"),"Historical group BCF retains mixed captured workflow and default owner");File.WriteAllText(Path.Combine(root,"historical-group-export-path.txt"),groupFile);
        var callbacks=new List<Action<InspectionScene?,string>>();snapshot.IsCurrent=true;
        var window=new DashboardWindow(vm,new DashboardActions {Preview=(id,done)=>callbacks.Add(done)});window.Show();window.SelectIssues();var grid=Find<DataGrid>(window).Single(g=>g.Columns.Any(c=>Equals(c.Header,"GroupId")));grid.SelectedIndex=0;Pump67();
        Check(callbacks.Count==1,"Panel automatically requests preview through injected API action");grid.SelectedIndex=1;Pump67();Check(callbacks.Count==2,"Changing selection requests the new identity");callbacks[0](null,"STALE_CALLBACK");Pump67();Check(!Find<TextBlock>(window).Any(t=>t.Text.Contains("STALE_CALLBACK")),"Late preview completion cannot replace a newer selected clash");
        var scene=new InspectionScene {Min=new Point3(0,0,0),Max=new Point3(2,2,2),Clash=new Point3(1,1,1),AxisA=new Point3(1,0,0),AxisB=new Point3(0,1,0),Notice="COPIED_SCENE_READY"};scene.Meshes.Add(new InspectionMesh(new[]{new Point3(0,0,0),new Point3(2,0,0),new Point3(0,2,2)},0));scene.Meshes.Add(new InspectionMesh(new[]{new Point3(0,0,1),new Point3(2,0,1),new Point3(0,2,1)},1));callbacks[1](scene,"");Pump67();
        Check(Find<ClashInspector>(window).Single().Mode=="compare"&&Find<TextBlock>(window).Any(t=>t.Text=="COPIED_SCENE_READY"),"Embedded panel displays copied 2D and 3D geometry");Capture45(window,root,"clash-panel-preview.png");window.SelectAnalytics();Capture45(window,root,"analytics.png");Check(Find<TabItem>(window).Any(t=>Equals(t.Header,"Analytics")&&t.IsEnabled),"Analytics view is enabled and rendered");
        var trendGrid=Find<DataGrid>(window).Single(g=>g.Columns.Any(c=>Equals(c.Header,"Version")));trendGrid.SelectedIndex=0;
        Check(Find<DataGrid>(window).Any(g=>ReferenceEquals(g,trendGrid))&&trendGrid.SelectedItem is ScanTrend,"Analytics trend selection survives routed child selection events");
        snapshot.IsCurrent=false;window.Refresh();window.SelectIssues();grid.SelectedIndex=0;Pump67();Check(callbacks.Count==2&&Find<TextBlock>(window).Any(t=>t.Text.Contains("Historical capture:")),"Historical panel explains stored-snapshot boundary without requesting current geometry");window.Close();callbacks[1](scene,"");Pump67();Check(true,"Closed panel ignores pending preview completion");
        source.Data=new DashboardSnapshot {DocumentKey="changed",IsCurrent=true};vm.DrillIds=new HashSet<string>{"one"};vm.Refresh();Check(vm.DrillIds==null,"Document switching clears Analytics identity scope");
        var large=Enumerable.Range(0,20000).Select(i=>{var r=Row("analytics-"+i);r.ElementAId=i+10;r.ElementBId=2;return r;}).ToList();var timer=System.Diagnostics.Stopwatch.StartNew();var big=DashboardAnalyticsService.Calculate(snapshot,large,new AnalyticsHistory(),DateTime.UtcNow);timer.Stop();Check(big.Metrics.Open==20000&&timer.ElapsedMilliseconds<5000,"20,000-row Analytics reconciles totals within synthetic ceiling");Console.WriteLine("Analytics 20,000 rows: "+timer.ElapsedMilliseconds+" ms");
    }
    private static void Pump67(){var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};timer.Tick+=(_,__)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    private sealed class AnalyticsSource:IDashboardDataSource,IDashboardAnalyticsSource
    {public DashboardSnapshot Data=new DashboardSnapshot();public AnalyticsHistory History=new AnalyticsHistory();public DashboardSnapshot Read(string scan,string comparison)=>Data;public string ReadHistory(string id)=>"Fixture history";public AnalyticsHistory ReadAnalytics(string scan)=>History;}
}
