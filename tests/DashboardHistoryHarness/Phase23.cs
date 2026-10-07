using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClashResolveAI.Core;
using ClashResolveAI.Engine;
using ClashResolveAI.ClashEngine;
using ClashResolveAI.Dashboard;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Dashboard.Application;
using ClashResolveAI.Dashboard.Persistence;
using Newtonsoft.Json;

internal static partial class Program
{
    private static ClashObservation Copy(ClashObservation c)=>JsonConvert.DeserializeObject<ClashObservation>(JsonConvert.SerializeObject(c))!;
    private static void Phase23(string root)
    {
        var compare=new ScanComparisonService();var first=ClashObservationAdapter.Capture(Issue());first.SeenInScanCount=1;
        ScanVersion Version(int n)=>new ScanVersion {ScanId="v"+n,SequenceNumber=n,Capture=Capture(),EndedAtUtc=DateTime.UtcNow.AddMinutes(n-10)};
        ScanSnapshot Snapshot(int n,params ClashObservation[] rows)=>new ScanSnapshot {Version=Version(n),Issues=rows};
        var baseline=Snapshot(1,first);var now=Copy(first);
        Check(compare.Compare(null,Snapshot(1,now)).Count(ClashChangeKind.New)==1,"First scan is an initial New baseline");
        now.ReopenedAtUtc=DateTime.UtcNow;Check(compare.Compare(null,Snapshot(1,now)).Count(ClashChangeKind.New)==1,"Initial baseline never advertises a historical recurrence");now.ReopenedAtUtc=null;
        Check(compare.Compare(baseline,Snapshot(2,now)).Count(ClashChangeKind.Persistent)==1,"Unchanged issue persists independently of workflow status");
        now.ObservationKind=ScanObservationKind.VerifiedAbsent;now.Status="Resolved";
        Check(compare.Compare(baseline,Snapshot(2,now)).Count(ClashChangeKind.Resolved)==1,"Verified comparable absence resolves comparison");
        Check(compare.Compare(Snapshot(2,now),Snapshot(3,Copy(now))).Count(ClashChangeKind.UnchangedResolved)==1,"Repeated absence does not inflate newly resolved counts");
        var returned=Copy(first);returned.Status="Reopened";returned.SeenInScanCount=2;
        Check(compare.Compare(Snapshot(3,now),Snapshot(4,returned)).Count(ClashChangeKind.Reopened)==1,"Recurrence after multiple absent scans is Reopened");
        returned.TestType="Unverified";Check(compare.Compare(Snapshot(3,now),Snapshot(4,returned)).Count(ClashChangeKind.Reopened)==0,"Inconclusive return does not claim confirmed recurrence");
        var changed=Copy(first);changed.Severity="Clearance";changed.TestType="ClearanceClash";changed.GapMm+=2;
        var flags=compare.Compare(baseline,Snapshot(2,changed)).Issues.Single().Flags;
        Check(flags.HasFlag(ClashChangeFlags.GeometryChanged)&&flags.HasFlag(ClashChangeFlags.SeverityDecreased)&&flags.HasFlag(ClashChangeFlags.ClassificationChanged),"Persistent issue carries overlapping evidence flags");
        changed=Copy(first);changed.GapMm+=0.05;changed.OverlapVolumeMm3+=0.5;
        Check(compare.Compare(baseline,Snapshot(2,changed)).Issues.Single().Flags==ClashChangeFlags.None,"Evidence tolerances suppress insignificant numeric changes");
        changed=Copy(first);changed.ElementAId=first.ElementBId;changed.ElementBId=first.ElementAId;changed.ElementUniqueIdA=first.ElementUniqueIdB;changed.ElementUniqueIdB=first.ElementUniqueIdA;changed.ElementSignatureA=first.ElementSignatureB;changed.ElementSignatureB=first.ElementSignatureA;
        Check(compare.Compare(baseline,Snapshot(2,changed)).Issues.Single().Flags==ClashChangeFlags.None,"Reversed endpoints preserve identity and signature comparison");
        changed.ElementUniqueIdA="replacement";Reject(()=>compare.Compare(baseline,Snapshot(2,changed)),"Reused numeric element identity is rejected");
        var different=Snapshot(2,now);different.Version.Capture.ConfigurationFingerprint="new rules";
        Check(compare.Compare(baseline,different).Count(ClashChangeKind.NotEvaluated)==1,"Rule changes cannot claim geometry resolution");
        var linked=Copy(first);linked.LinkInstanceB="link-1";linked.NormalizedKey="linked-pair";
        var linkedBefore=Snapshot(1,linked);linkedBefore.Version.Capture.LinkedModelsJson="[{\"InstanceUniqueId\":\"link-1\",\"IsLoaded\":true,\"DocumentKey\":\"linked-doc\",\"Transform\":[1]}]";
        var absentLink=Copy(linked);absentLink.ObservationKind=ScanObservationKind.VerifiedAbsent;
        var linkedNow=Snapshot(2,absentLink);linkedNow.Version.Capture.LinkedModelsJson=linkedBefore.Version.Capture.LinkedModelsJson;
        Check(compare.Compare(linkedBefore,linkedNow).Count(ClashChangeKind.Resolved)==1,"Comparable loaded-link absence can resolve physical finding");
        foreach(var environment in new[]{linkedBefore.Version.Capture.LinkedModelsJson.Replace("true","false"),linkedBefore.Version.Capture.LinkedModelsJson.Replace("linked-doc","replacement-doc"),linkedBefore.Version.Capture.LinkedModelsJson.Replace("[1]","[2]")}){
            linkedNow.Version.Capture.LinkedModelsJson=environment;Check(compare.Compare(linkedBefore,linkedNow).Count(ClashChangeKind.NotEvaluated)==1,"Unloaded, replaced or transformed linked model cannot establish resolution");
        }
        Check(compare.Compare(baseline,Snapshot(2)).Count(ClashChangeKind.MissingFromScan)==1,"Missing observation is not silently resolved");
        different=Snapshot(2,first);different.Version.Capture.DocumentKey="other";Reject(()=>compare.Compare(baseline,different),"Cross-document comparisons are rejected");Reject(()=>compare.Compare(Snapshot(2,first),baseline),"Reverse chronological comparison rejected");
        changed=Copy(first);changed.SeenInScanCount=3;changed.DetectedAt=DateTime.UtcNow.AddYears(-1);
        Check(compare.Compare(Snapshot(1),Snapshot(2,changed)).Count(ClashChangeKind.NotEvaluated)==1,"Known identity outside baseline is not falsely New");
        changed.TimestampKind="LegacyLocalUnknown";changed.DetectedAtOriginalText="legacy time";changed.SeenInScanCount=1;
        Check(compare.Compare(Snapshot(1),Snapshot(2,changed)).Count(ClashChangeKind.NotEvaluated)==1,"Imported known identity with unknown time is not falsely New");
        foreach(var state in new[]{"Ignored","Closed"}) {
            var old=Issue();old.Status=(ClashStatus)Enum.Parse(typeof(ClashStatus),state);
            var staged=FullScanSnapshotService.Stage(new[]{old},new[]{Issue()},new ScanStatistics {Mode=ScanMode.HardOnly,Scope=new ScanScope("fixture")},99);
            Check(staged.Rows.Single().Status.ToString()==state,"Recurrence preserves suppressed "+state+" workflow");
        }
        var resolved=Issue();resolved.Status=ClashStatus.Resolved;
        var batch=FullScanSnapshotService.Stage(new[]{resolved},new[]{Issue()},new ScanStatistics {Mode=ScanMode.HardOnly,Scope=new ScanScope("fixture")},99);
        Check(batch.Rows.Single().Status==ClashStatus.Reopened&&batch.Observations.Single().ReopenedAtUtc.HasValue,"Full Scan staging reopens confirmed resolved issue and timestamps episode");
        var scope=new ScanScope("fixture");scope.Note(1,"","");var stats=new ScanStatistics {Mode=ScanMode.HardOnly,Scope=scope};
        var prior=Issue();prior.SeenInScanCount=4;prior.ConsecutiveScanCount=2;
        batch=FullScanSnapshotService.Stage(new[]{prior},Array.Empty<ClashResult>(),stats,99);
        Check(batch.Observations.Single().ConsecutiveScanCount==0&&batch.Observations.Single().SeenInScanCount==4,"Verified absence resets consecutive count and preserves observed count");
        stats.Scope=new ScanScope("fixture");batch=FullScanSnapshotService.Stage(new[]{prior},Array.Empty<ClashResult>(),stats,99);
        Check(batch.Observations.Single().ConsecutiveScanCount==2,"Out-of-scope scan is neutral for consecutive observations");
        var manual=Copy(first);manual.Status="Resolved";
        Check(DashboardMetricPolicy.Calculate(new[]{manual},null,null).Health==0,"Manual workflow resolution is not physical health");
        Check(DashboardMetricPolicy.Calculate(new[]{now},null,null).Health==100,"Verified physical absence produces health evidence");
        manual.TestType="Unverified";Check(DashboardMetricPolicy.Calculate(new[]{manual},null,null).Health==null,"No confirmed eligible evidence has no health percentage");
        var onsite=Copy(first);onsite.Status="OnSite";Check(DashboardMetricPolicy.Calculate(new[]{onsite},null,null).Open==1,"OnSite is actionable");
        onsite.TestType="Unverified";Check(DashboardMetricPolicy.Calculate(new[]{onsite},null,null).Critical==0,"Unverified candidates do not inflate confirmed critical counts");
        Check((int)ClashStatus.Closed==7&&(int)ClashStatus.Reopened==8,"Persisted original enum values remain stable");
        Check(LifecycleCommandService.Allowed("Closed").SequenceEqual(new[]{ClashStatus.Reopened})&&!LifecycleCommandService.Allowed("Resolved").Contains(ClashStatus.Active),"Closed/Resolved workflow cannot bypass explicit reopen");
        var run=Path.Combine(root,"phase23-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(run);Environment.SetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR",run);
        using var db=new ClashDatabase();db.Open("fixture");
        var service=new FullScanDashboardService(db);
        FullScanSnapshotBatch Scan(params ClashResult[] rows){var version=db.History.BeginScan(Capture());var s=new ScanStatistics {ScanId=version.ScanId,Mode=ScanMode.HardOnly,Scope=new ScanScope("fixture")};s.Scope.Note(1,"","");return service.Complete(db.LoadCurrentClashes(),rows.ToList(),s,99);}
        batch=Scan(Issue());var v1=db.History.GetVersions().Last();Check(batch.Comparison.Count(ClashChangeKind.New)==1,"Production completion establishes initial baseline");
        batch=Scan();Check(batch.Rows.Single().Status==ClashStatus.Resolved&&batch.Comparison.Count(ClashChangeKind.Resolved)==1,"Production completion stages and commits reliable resolution");
        Scan();batch=Scan(Issue());Check(batch.Rows.Single().Status==ClashStatus.Reopened&&batch.Observations.Single().SeenInScanCount==2&&batch.Observations.Single().ConsecutiveScanCount==1,"Production recurrence preserves history and counters across multiple absences");
        Check(db.History.GetEvents("issue").Count(e=>e.Origin=="Scan")==3,"Initial state, automatic resolution and recurrence each have scan audit events");
        var commands=new LifecycleCommandService(db);var eventCount=db.History.GetEvents("issue").Count;
        Reject(()=>commands.ChangeStatus(new[]{"issue"},ClashStatus.Closed,"tester",""),"Reason-required workflow command rejected before commit");
        Reject(()=>commands.ChangeStatus(new[]{"issue","missing"},ClashStatus.Active,"tester",""),"Bulk prevalidation rejects unavailable member");
        Check(db.History.FindCurrent("issue")!.Status=="Reopened"&&db.History.GetEvents("issue").Count==eventCount,"Failed workflow commands leave memory/storage event history intact");
        commands.ChangeStatus(new[]{"issue"},ClashStatus.Active,"tester","");commands.ChangeStatus(new[]{"issue"},ClashStatus.Active,"tester","");
        Check(db.History.GetEvents("issue").Count==eventCount+1,"No-op workflow commands add no duplicate events");
        commands.UpdateMetadata(new[]{"issue"},"Coordinator",DateTime.UtcNow.Date,"Review bend","tester");
        Check(db.History.FindCurrent("issue")!.MetadataJson.Contains("Coordinator")&&db.History.GetEvents("issue").Last().Origin=="UserMetadata","Owner, due and comment persist with coordination audit");
        var stale=Copy(db.History.FindCurrent("issue")!);var mutation=new IssueMutation {Snapshot=stale,ExpectedStatus=stale.Status,ExpectedMetadataJson=stale.MetadataJson};commands.ChangeStatus(new[]{"issue"},ClashStatus.InReview,"tester","");stale.Status="Closed";
        Reject(()=>db.CommitMutations(new[]{mutation}),"Optimistic concurrency prevents overwriting changed workflow");
        var second=Issue("second",3);second.Status=ClashStatus.InReview;db.UpsertClash(second);
        string path=Path.Combine(run,"databases","fixture.clash.db");
        using(var connection=new System.Data.SQLite.SQLiteConnection("Data Source="+path)){connection.Open();Sql(connection,"CREATE TRIGGER fail_bulk_event BEFORE INSERT ON ClashLifecycleEvents WHEN NEW.ClashId='second' BEGIN SELECT RAISE(ABORT,'injected bulk failure'); END;");}
        eventCount=db.History.GetEvents("issue").Count;
        Reject(()=>commands.ChangeStatus(new[]{"issue","second"},ClashStatus.Closed,"tester","bulk close"),"Late failure rolls back entire bulk command");
        Check(db.History.FindCurrent("issue")!.Status=="InReview"&&db.History.FindCurrent("second")!.Status=="InReview"&&db.History.GetEvents("issue").Count==eventCount,"Bulk rollback preserves both issues and first-member audit history");
        using(var connection=new System.Data.SQLite.SQLiteConnection("Data Source="+path)){connection.Open();Sql(connection,"DROP TRIGGER fail_bulk_event");}
        commands.ChangeStatus(new[]{"issue","second"},ClashStatus.Closed,"tester","bulk close");
        Check(db.History.GetEvents("issue").Last().ToStatus=="Closed"&&db.History.GetEvents("second").Last().ToStatus=="Closed","Successful bulk status command audits every changed issue");
        commands.ChangeStatus(new[]{"issue"},ClashStatus.Reopened,"tester","review again");commands.ChangeStatus(new[]{"issue"},ClashStatus.InReview,"tester","");db.DeleteClashes(new[]{"second"});
        var source=new DashboardDataSource(db,()=>"fixture",id=>false);var vm=new DashboardWorkspace(source,commands);vm.Refresh();
        var persisted=db.History.FindCurrent("issue")!;var originalJson=JsonConvert.SerializeObject(db.History.GetObservations(v1.ScanId));
        var legacyCounter=Copy(persisted);legacyCounter.SeenInScanCount=0;db.UpsertClash(ClashObservationAdapter.Restore(legacyCounter));
        Check(db.History.GetKnown().Single(r=>r.ClashId=="issue").SeenInScanCount==2&&JsonConvert.SerializeObject(db.History.GetObservations(v1.ScanId))==originalJson,"Phase 1 counter backfill derives observations without rewriting immutable history");
        db.UpsertClash(ClashObservationAdapter.Restore(persisted));
        Check(vm.Snapshot.IsCurrent&&vm.Snapshot.Metrics.LastScanUtc==vm.Snapshot.Selected!.EndedAtUtc,"Workspace uses actual persisted completion time");
        vm.Filters["Change"]="Reopened";Check(vm.Visible().Count==1,"Overview comparison filter agrees with saved version outcome");
        vm.SelectedId="issue";vm.Refresh();Check(vm.Filters["Change"]=="Reopened"&&vm.SelectedId=="issue","Refresh preserves filters and issue selection");
        vm.ScanId=v1.ScanId;vm.Refresh();Check(!vm.Snapshot.IsCurrent&&vm.Snapshot.Rows.Single().Status=="InReview","Historical scan retains original workflow after current edits");
        Reject(()=>vm.ChangeStatus(new[]{"issue"},ClashStatus.Closed,"test"),"Historical mutation blocked at application boundary");
        Check(vm.Allowed(new[]{"issue"}).Count==0,"Historical next-action controls have no allowed transitions");
        vm.ScanId="";vm.Filters.Clear();vm.Refresh();
        UiAcceptance(vm,root);
        var exportRow=Copy(vm.Snapshot.Rows.Single());exportRow.Status="Closed";
        var topics=DashboardExportProjection.IssueTopics(new[]{exportRow});
        string bcf=new ClashResolveAI.Services.BcfExportService().ExportGroups(topics,"Phase23 issue export",root);
        var topic=new ClashResolveAI.Services.BcfExportService().ImportBcf(bcf).Single();
        Check(topic.TopicStatus=="Closed"&&topic.AssignedTo=="Coordinator"&&topic.Description.Contains("Review bend"),"Issue BCF export preserves current status, owner and comment");
        Check(exportRow.MetadataJson==vm.Snapshot.Rows.Single().MetadataJson,"Export projections do not mutate workspace metadata");
        File.WriteAllText(Path.Combine(root,"export-path.txt"),bcf);
        var isolated=new MemorySource {Data=vm.Snapshot};var isolatedVm=new DashboardWorkspace(isolated,commands);isolatedVm.Refresh();isolatedVm.Filters["Status"]="InReview";isolatedVm.Search="duct";isolatedVm.SelectedId="issue";
        isolated.Data=new DashboardSnapshot {DocumentKey="another project"};isolatedVm.Refresh();Check(isolatedVm.Filters.Count==0&&isolatedVm.Search==""&&isolatedVm.SelectedId=="","Document switch isolates filters and issue selection");
        db.Dispose();db.Open("fixture");Check(db.History.FindCurrent("issue")!.SeenInScanCount==2&&db.History.FindCurrent("issue")!.Status=="InReview","Counters, metadata and workflow survive database reopen");
    }
    private static void UiAcceptance(DashboardWorkspace vm,string root)
    {
        var window=new DashboardWindow(vm,new DashboardActions());window.Show();window.UpdateLayout();
        void Capture(string file){window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(()=>{}));window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(root,file));encoder.Save(output);}
        Capture("overview.png");window.SelectIssues();Capture("issues.png");
        Check(Find<DataGrid>(window).Single(g=>g.Columns.Any(c=>Equals(c.Header,"GroupId"))).EnableRowVirtualization,"Issues grid preserves row virtualization");
        Check(Find<TabItem>(window).Count(t=>!t.IsEnabled)==0,"All five workspaces are available");
        var grid=Find<DataGrid>(window).Single(g=>g.Columns.Any(c=>Equals(c.Header,"GroupId")));grid.SelectedIndex=0;window.UpdateLayout();Capture("issue-detail.png");
        Check(Find<TabItem>(window).Any(t=>Equals(t.Header,"Evidence"))&&Find<TabItem>(window).Any(t=>Equals(t.Header,"Comments")),"Shared issue detail exposes evidence and coordination tabs");
        window.Close();
        var large=Enumerable.Range(0,20000).Select(i=>{var r=Copy(vm.Snapshot.Rows[0]);r.ClashId="load-"+i;r.NormalizedKey=r.ClashId;return r;}).ToList();
        var watch=Stopwatch.StartNew();var sorted=DashboardMetricPolicy.Rank(large).ToList();watch.Stop();Check(sorted.Count==20000&&watch.ElapsedMilliseconds<5000,"20,000-row ranking completes within 5-second harness ceiling");Console.WriteLine("Ranking 20,000 rows: "+watch.ElapsedMilliseconds+" ms");
        var source=new MemorySource {Data=new DashboardSnapshot {DocumentKey="large",IsCurrent=true,Rows=large}};
        var largeVm=new DashboardWorkspace(source,new LifecycleCommandService(new NoCommands()));watch.Restart();
        var largeWindow=new DashboardWindow(largeVm,new DashboardActions());largeWindow.Show();largeWindow.SelectIssues();largeWindow.UpdateLayout();watch.Stop();
        var loadGrid=Find<DataGrid>(largeWindow).Single(g=>g.Columns.Any(c=>Equals(c.Header,"GroupId")));Check(loadGrid.Items.Count==20000&&VisualRows(loadGrid)<100,"20,000-row WPF grid realizes only visible rows");
        Console.WriteLine("20,000-row window construction / layout: "+watch.ElapsedMilliseconds+" ms");largeWindow.Close();
    }
    private sealed class MemorySource:IDashboardDataSource {public DashboardSnapshot Data=new DashboardSnapshot();public DashboardSnapshot Read(string scan,string comparison)=>Data;public string ReadHistory(string id)=>"Fixture history";}
    private sealed class NoCommands:IClashCommandStore {public ClashObservation? ReadCurrentIssue(string id)=>null;public void CommitMutations(IReadOnlyList<IssueMutation> mutations)=>throw new InvalidOperationException();}
    private static int VisualRows(DependencyObject parent){int rows=parent is DataGridRow?1:0;for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)rows+=VisualRows(VisualTreeHelper.GetChild(parent,i));return rows;}
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject
    {if(parent is T match)yield return match;foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())foreach(var item in Find<T>(child))yield return item;}
}
