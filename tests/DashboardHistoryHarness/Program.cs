using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using ClashResolveAI.ClashEngine;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Dashboard.Persistence;
using ClashResolveAI.Dashboard.Application;
using Newtonsoft.Json;

internal static partial class Program
{
    private static int _checks;
    private static void Check(bool condition,string label) {if(!condition)throw new Exception(label);Console.WriteLine("PASS "+label);_checks++;}
    private static void Reject(Action action,string label) {bool failed=false;try{action();}catch{failed=true;}Check(failed,label);}
    private static void Sql(SQLiteConnection c,string sql) {using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}
    private static long Count(SQLiteConnection c,string sql) {using var cmd=c.CreateCommand();cmd.CommandText=sql;return Convert.ToInt64(cmd.ExecuteScalar());}
    private static ScanCapture Capture(string doc="fixture")=>new ScanCapture {DocumentKey=doc,DisplayName="Fixture",ConfigurationFingerprint="rules-1",ConfigurationJson="{\"Mode\":\"HardOnly\"}",ScopeJson="{\"LevelId\":\"L1\"}",ScanMode="HardOnly",LinkedModelsJson="[{\"IsLoaded\":false}]"};
    private static ClashResult Issue(string id="issue",long a=1)=>new ClashResult {
        ClashId=id,HostDocumentKey="fixture",ElementAId=a,ElementBId=2,ElementUniqueIdA="unique-a",ElementUniqueIdB="unique-b",
        ElementSignatureA="version-a",ElementSignatureB="version-b",DisciplineA=Discipline.HVAC,DisciplineB=Discipline.Structural,
        CategoryNameA="Duct",CategoryNameB="Beam",FamilyTypeA="400x200",Priority="Critical",Severity=ClashSeverity.Critical,
        TestType=ClashTestType.HardClash,Status=ClashStatus.InReview,OverlapVolumeMM3=1240.125,GapMM=12.25,RequiredClearanceMM=50.5,
        GeometryEvidence="Confirmed solid intersection",ClashPoint=new XYZ(1.125,2.25,3.5),LevelName="L1",GridRef="A/1",
        Metadata=new ClashMetadata {AssignedEngineer="HVAC team",DueDate=new DateTime(2026,10,9,0,0,0,DateTimeKind.Utc),Comments="Preserve me"},GeometryRevision=17
    };

    [STAThread]
    public static void Main(string[] args)
    {
        string root=Path.GetFullPath(args.Length>0?args[0]:Path.Combine("verification","dashboard-phase01","history-tests"));Directory.CreateDirectory(root);
        CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");
        LegacyMigration(root);
        PublicationAndRestart(root);
        Staging();
        Phase23(root);
        Phase45(root);
        Phase67(root);
        RecordedFixture(root);
        FailedMigration(root);
        Console.WriteLine(_checks+" dashboard persistence checks passed (SQLite and copied-data tests; no native Revit geometry).");
    }

    private static void LegacyMigration(string root)
    {
        string path=Path.Combine(root,"legacy-"+Guid.NewGuid().ToString("N")+".db");
        using var conn=new SQLiteConnection("Data Source="+path);conn.Open();
        Sql(conn,@"CREATE TABLE Clashes(ClashId TEXT PRIMARY KEY,ElementAId INTEGER,ElementBId INTEGER,Status TEXT,MetadataJson TEXT,DetectedAt TEXT,ClashPoint TEXT,HostDocumentKey TEXT,Origin TEXT);
INSERT INTO Clashes VALUES('legacy-issue',3000000000,4000000000,'Resolved','{""AssignedEngineer"":""Engineer A"",""Comments"":""Legacy comment""}','2026-10-01T10:00:00','1.25,2.5,3.75','fixture','Full');
CREATE TABLE ClashRevisions(Id INTEGER PRIMARY KEY,ClashId TEXT,Timestamp TEXT,Author TEXT,OldStatus TEXT,NewStatus TEXT,Comment TEXT);
INSERT INTO ClashRevisions VALUES(7,'legacy-issue','2026-10-02T12:00:00','User','Active','Resolved','Fixed');");
        Sql(conn,@"ALTER TABLE Clashes ADD COLUMN GroupId TEXT;
UPDATE Clashes SET GroupId='group-1';
CREATE TABLE ClashGroups(GroupId TEXT PRIMARY KEY,GroupTitle TEXT,GroupingReason TEXT,MetadataJson TEXT);
INSERT INTO ClashGroups VALUES('group-1','Legacy duct route','Same Route Path','{""AssignedEngineer"":""Route owner""}');");
        var repository=new ScanHistoryRepository(conn,"fixture");repository.Initialize(path,true);
        Check(File.Exists(repository.MigrationBackupPath),"Existing database is backed up before migration");
        using(var backup=new SQLiteConnection("Data Source="+repository.MigrationBackupPath)){backup.Open();Check(Count(backup,"SELECT COUNT(*) FROM Clashes")==1&&Count(backup,"SELECT COUNT(*) FROM sqlite_master WHERE name='ScanVersions'")==0,"Backup contains original schema/data and is restorable");}
        string restoredPath=path+".restored";File.Copy(repository.MigrationBackupPath,restoredPath);
        using(var restored=new SQLiteConnection("Data Source="+restoredPath)){
            restored.Open();bool original=Count(restored,"SELECT COUNT(*) FROM Clashes WHERE ElementAId=3000000000 AND Status='Resolved'")==1&&Count(restored,"SELECT COUNT(*) FROM sqlite_master WHERE name='ScanVersions'")==0;
            var restoredRepository=new ScanHistoryRepository(restored,"fixture");restoredRepository.Initialize(restoredPath,true);
            Check(original&&restoredRepository.GetCurrent().Single().MetadataJson.Contains("Legacy comment")&&restoredRepository.GetEvents("legacy-issue").Count==1&&restoredRepository.GetGroups(restoredRepository.GetVersions().Single().ScanId).Single().MetadataJson.Contains("Route owner"),"Copied migration backup reopens with original schema and safely remigrates issue, event and group data");
        }
        var row=repository.GetCurrent().Single();
        Check(row.ElementAId==3000000000&&row.ElementBId==4000000000&&row.Status=="Resolved","Migration preserves 64-bit identities and resolved state");
        Check(row.MetadataJson.Contains("Engineer A")&&row.MetadataJson.Contains("Legacy comment"),"Migration preserves assignment/comments");
        Check(row.TimestampKind=="LegacyLocalOrUnknown"&&row.X==1.25,"Legacy time provenance and invariant coordinates survive migration");
        Check(repository.GetVersions().Single().State==ScanVersionState.LegacyImported,"Legacy current rows are labelled imports, not fabricated completed scans");
        Check(repository.GetEvents("legacy-issue").Single().TimestampKind=="LegacyLocalOrUnknown","Legacy revision events are imported without guessed timezone");
        Check(row.DetectedAtOriginalText=="2026-10-01T10:00:00","Migration preserves the original legacy timestamp text");
        Check(repository.GetGroups(repository.GetVersions().Single().ScanId).Single().MetadataJson.Contains("Route owner"),"Legacy group metadata and memberships are preserved");
        repository.Initialize(path,true);
        Check(repository.GetVersions().Count==1&&repository.GetEvents("legacy-issue").Count==1,"Migration is idempotent without duplicate events/scans");
        Reject(()=>repository.BeginScan(Capture("other")),"Cross-document scan attempts are rejected");
        var attempt=repository.BeginScan(Capture());repository.EndAttempt(attempt.ScanId,ScanVersionState.Cancelled,"Cancelled by user");
        Check(repository.GetVersions().Last().State==ScanVersionState.Cancelled&&repository.GetObservations(attempt.ScanId).Count==0,"Cancelled scan retains reason and does not publish geometry");
        var interrupted=repository.BeginScan(Capture());repository.RecoverInterruptedAttempts();
        Check(repository.GetVersions().Last().State==ScanVersionState.Failed,"Interrupted running attempt is recovered as failed");
        Sql(conn,"UPDATE DashboardSchema SET Version=99");
        Reject(()=>repository.Initialize(path,true),"Newer unsupported schemas are rejected");
    }

    private static void PublicationAndRestart(string root)
    {
        string run=Path.Combine(root,"publication-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(run);Environment.SetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR",run);
        using var db=new ClashDatabase();db.Open("fixture");
        var original=Issue();db.UpsertClash(original);
        var snapshot=ClashObservationAdapter.Capture(original);
        original.Metadata.Comments="Edited after capture";
        Check(snapshot.MetadataJson.Contains("Preserve me")&&!snapshot.MetadataJson.Contains("Edited after capture"),"Captured metadata is detached from mutable issue metadata");
        Check(snapshot.X==1.125&&snapshot.RequiredClearanceMm==50.5&&snapshot.ElementSignatureA=="version-a","Copied evidence includes coordinates, clearance and element signatures");
        Check(typeof(ClashObservation).GetProperties().All(p=>p.PropertyType.Namespace!="Autodesk.Revit.DB"),"Persistence DTO has no Revit API properties");
        var version=db.History.BeginScan(Capture());
        var stats=new ScanStatistics {ScanId=version.ScanId,Mode=ScanMode.HardOnly,Scope=new ScanScope("fixture")};stats.Scope.Note(1,"","");
        var batch=FullScanSnapshotService.Stage(db.LoadCurrentClashes(),new[]{Issue()},stats,18);
        db.CommitFullScan(stats,batch.Rows,batch.Observations,batch.Groups);
        Check(db.History.GetVersions().Single().State==ScanVersionState.Completed,"Scan and current results publish as a completed version");
        Check(db.History.GetVersions().Single().StartedAtUtc.Kind==DateTimeKind.Utc&&db.History.GetVersions().Single().EndedAtUtc!.Value.Kind==DateTimeKind.Utc,"New scan timestamps are UTC");
        Check(db.History.GetGroups(version.ScanId).Single().MemberClashIds.Single()=="issue","Saved group membership references immutable issue revisions");
        Check(db.History.GetObservations(version.ScanId).Single().GroupId==db.History.GetGroups(version.ScanId).Single().GroupKey,"Historical issue group reference matches its saved group key");
        string oldJson=JsonConvert.SerializeObject(db.History.GetObservations(version.ScanId));
        db.UpdateClashStatus("issue",ClashStatus.Resolved,"Engineer","Fixed duct");
        Check(db.LoadCurrentClashes().Single().Status==ClashStatus.Resolved&&db.History.GetEvents("issue").Single().Origin=="User","User state and audit event commit together");
        Check(JsonConvert.SerializeObject(db.History.GetObservations(version.ScanId))==oldJson,"Status edit does not change historical scan state/evidence");
        string databasePath=Path.Combine(run,"databases","fixture.clash.db");
        using(var c=new SQLiteConnection("Data Source="+databasePath)){c.Open();
            Reject(()=>Sql(c,"UPDATE ClashScanRevisions SET SnapshotJson='{}'"),"Database rejects historical observation updates");
            Reject(()=>Sql(c,"DELETE FROM ScanVersions WHERE State='Completed'"),"Database rejects completed version deletion");
            Reject(()=>Sql(c,"UPDATE GroupScanRevisions SET SnapshotJson='{}'"),"Database rejects historical group updates");
            Reject(()=>Sql(c,"DELETE FROM GroupScanMembers"),"Database rejects saved membership deletion");
            Reject(()=>Sql(c,"UPDATE ClashLifecycleEvents SET Comment='changed'"),"Database rejects audit event changes");
        }
        var failed=db.History.BeginScan(Capture());
        var failedStats=new ScanStatistics {ScanId=failed.ScanId,Mode=ScanMode.HardOnly,Scope=new ScanScope("fixture")};failedStats.Scope.Note(1,"","");
        var changed=Issue();changed.OverlapVolumeMM3=99;changed.Status=ClashStatus.Active;
        var failedBatch=FullScanSnapshotService.Stage(db.LoadCurrentClashes(),new[]{changed},failedStats,19);
        using(var c=new SQLiteConnection("Data Source="+databasePath)){c.Open();Sql(c,"CREATE TRIGGER injected_write_failure BEFORE INSERT ON GroupScanRevisions BEGIN SELECT RAISE(ABORT,'Injected disk write failure'); END;");}
        Reject(()=>db.CommitFullScan(failedStats,failedBatch.Rows,failedBatch.Observations,failedBatch.Groups),"Injected late write failure aborts full scan publication");
        Check(db.History.GetObservations(failed.ScanId).Count==0&&db.History.GetEvents("issue").Count==1&&db.LoadCurrentClashes().Single().Status==ClashStatus.Resolved,"Rollback removes staged revisions/events and preserves current status");
        Check(db.LoadCurrentClashes().Single().OverlapVolumeMM3==1240.125&&db.History.GetVersions().Last().State==ScanVersionState.Running,"Rollback preserves current evidence and does not mark failed save completed");
        using(var c=new SQLiteConnection("Data Source="+databasePath)){c.Open();Check(Count(c,"SELECT COUNT(*) FROM Clashes WHERE Status='Resolved'")==1,"Compatibility current rows participate in the same rollback");Sql(c,"DROP TRIGGER injected_write_failure");}
        db.History.EndAttempt(failed.ScanId,ScanVersionState.Failed,"Injected disk write failure");
        db.UpdateClashStatuses(new[]{"issue"},ClashStatus.Closed,"User","Bulk close");
        Check(db.History.GetEvents("issue").Count==2,"Bulk status path records audit events");
        Reject(()=>db.UpdateClashStatuses(new[]{"issue","missing"},ClashStatus.Active,"User","Atomic test"),"Invalid member aborts the whole bulk update");
        Check(db.LoadCurrentClashes().Single().Status==ClashStatus.Closed&&db.History.GetEvents("issue").Count==2,"Bulk rollback preserves current state and event count");
        db.DeleteClashes(new[]{"issue"});
        Check(db.LoadCurrentClashes().Count==0&&db.GetClashCount()==0&&db.History.GetObservations(version.ScanId).Count==1&&db.History.GetEvents("issue").Count==2,"Purge archives current results and preserves scans/events");
        db.Dispose();db.Open("fixture");Check(db.LoadCurrentClashes().Count==0,"Archived current results stay hidden after reopening");
        db.UpsertClash(Issue());
        Check(db.LoadCurrentClashes().Count==1&&db.History.GetEvents("issue").Count==2,"Rediscovered archived pair is restored without losing history");
        db.Dispose();db.Open("fixture");
        var reopened=db.LoadCurrentClashes().Single();
        Check(reopened.ElementUniqueIdA=="unique-a"&&reopened.Metadata.AssignedEngineer=="HVAC team"&&reopened.RequiredClearanceMM==50.5,"Reopen restores full current metadata and physical evidence");
        Check(reopened.GeometryRevision==-1&&reopened.ElementA==null,"Reopened current results have no live handles and require current-session verification");
        Check(JsonConvert.SerializeObject(db.History.GetObservations(version.ScanId))==oldJson,"Historical snapshots survive restart unchanged");
        Check(db.History.GetVersions().Count(v=>v.State==ScanVersionState.Completed)==1,"Failed attempt never becomes the completed comparison baseline");
        Reject(()=>db.CommitFullScan(stats,batch.Rows,batch.Observations,batch.Groups),"Completed scan cannot be published a second time");
        var conflicting=ClashObservationAdapter.Capture(Issue());conflicting.NormalizedKey="different pair";
        Reject(()=>db.History.UpsertCurrent(conflicting),"Existing clash identity cannot be reassigned to another pair");
        var absentVersion=db.History.BeginScan(Capture());
        var absentStats=new ScanStatistics {ScanId=absentVersion.ScanId,Mode=ScanMode.HardOnly,Scope=new ScanScope("fixture")};absentStats.Scope.Note(1,"","");
        var absent=FullScanSnapshotService.Stage(db.LoadCurrentClashes(),Array.Empty<ClashResult>(),absentStats,20);
        db.CommitFullScan(absentStats,absent.Rows,absent.Observations,absent.Groups);
        Check(db.History.GetObservations(absentVersion.ScanId).Single().ObservationKind==ScanObservationKind.VerifiedAbsent&&db.LoadCurrentClashes().Single().Status==ClashStatus.Resolved,"Previously persisted issue absent from a later scan is retained with an absence revision");
        Check(db.History.GetEvents("issue").Last().Origin=="Scan","Automatic scan status changes append scan-origin lifecycle events");
        using(var c=new SQLiteConnection("Data Source="+databasePath)){c.Open();Check(Count(c,"PRAGMA foreign_key_check")==0,"Published database has no foreign-key violations");}
    }

    private static void Staging()
    {
        var prior=Issue();var stats=new ScanStatistics {Mode=ScanMode.HardOnly,Scope=new ScanScope("fixture")};stats.Scope.Note(1,"","");
        var result=FullScanSnapshotService.Stage(new[]{prior},Array.Empty<ClashResult>(),stats,18);
        Check(prior.Status==ClashStatus.InReview&&prior.GroupId==""&&prior.Metadata.Comments=="Preserve me","Staging does not mutate published status/group/metadata before commit");
        Check(result.Observations.Single().ObservationKind==ScanObservationKind.VerifiedAbsent,"Reliable evaluated absence is recorded separately from detected geometry");
        prior.TestType=ClashTestType.ClearanceClash;
        result=FullScanSnapshotService.Stage(new[]{prior},Array.Empty<ClashResult>(),stats,18);
        Check(result.Rows.Single().Status==ClashStatus.InReview&&result.Observations.Single().ObservationKind==ScanObservationKind.NotEvaluated,"HardOnly does not resolve retained clearance and records why");
        prior.TestType=ClashTestType.HardClash;stats.Scope.MissingPair(prior.NormalizedKey);
        result=FullScanSnapshotService.Stage(new[]{prior},Array.Empty<ClashResult>(),stats,18);
        Check(result.Rows.Single().Status==ClashStatus.InReview&&result.Observations.Single().ObservationKind==ScanObservationKind.NotEvaluated,"Missing pair geometry preserves issue state");
        stats.Scope=new ScanScope("fixture");stats.Scope.Note(1,"","unloaded-link");prior.LinkInstanceB="loaded-link";
        result=FullScanSnapshotService.Stage(new[]{prior},Array.Empty<ClashResult>(),stats,18);
        Check(result.Observations.Single().ObservationKind==ScanObservationKind.NotEvaluated,"Coverage partitions distinguish unavailable link instances");
        var uncertain=Issue("uncertain");uncertain.TestType=ClashTestType.Unverified;uncertain.UnverifiedReason=UnverifiedReason.SolidTest;
        result=FullScanSnapshotService.Stage(Array.Empty<ClashResult>(),new[]{uncertain},stats,18);
        Check(result.Observations.Single().TestType=="Unverified"&&result.Observations.Single().UnverifiedReason=="SolidTest","Observed uncertainty remains unverified evidence");
        Check(result.Groups.Single().GroupKey==FullScanSnapshotService.Stage(Array.Empty<ClashResult>(),new[]{uncertain},stats,18).Groups.Single().GroupKey,"Captured exact-membership group key is deterministic");
        var live=Issue("live");live.Origin=ResultOrigin.Live;
        Check(FullScanSnapshotService.Stage(Array.Empty<ClashResult>(),new[]{live},stats,18).Observations.Count==0,"Live-origin results cannot become Full Scan observations");
    }

    private static void RecordedFixture(string root)
    {
        string path=Path.Combine(Path.GetDirectoryName(root)!,"reference-production-scan.json");
        if(!File.Exists(path)){Console.WriteLine("SKIP optional recorded production fixture: provide an authorized reference-production-scan.json beside the history-tests directory.");return;}
        var fixture=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
        var rows=fixture["results"]!.ToObject<List<ClashResult>>()!;
        foreach(var row in rows)row.HostDocumentKey="recorded-fixture";
        var staged=FullScanSnapshotService.Stage(Array.Empty<ClashResult>(),rows,new ScanStatistics {Mode=ScanMode.HardAndClearance,Scope=new ScanScope("recorded-fixture")},10);
        var restored=staged.Observations.Select(ClashObservationAdapter.Restore).ToDictionary(c=>c.ClashId);
        Check(rows.Count>1000&&restored.Count==rows.Count,"Recorded production fixture retains every issue identity through snapshot staging");
        Check(rows.All(r=> {var c=restored[r.ClashId];return r.NormalizedKey==c.NormalizedKey&&r.TestType==c.TestType&&r.Severity==c.Severity&&r.UnverifiedReason==c.UnverifiedReason&&r.GeometryEvidence==c.GeometryEvidence&&r.GapMM==c.GapMM&&r.OverlapVolumeMM3==c.OverlapVolumeMM3&&r.RequiredClearanceMM==c.RequiredClearanceMM&&r.CategoryNameA==c.CategoryNameA&&r.FamilyTypeB==c.FamilyTypeB;}),"Recorded fixture round trip preserves classification, uncertainty, numeric evidence and labels");
        Console.WriteLine("Recorded fixture: "+rows.Count+" issues; no new Revit scan was performed.");
        string? previousDirectory=Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR");
        try {
            Environment.SetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR",Path.Combine(root,"recorded-performance"));
            using var database=new ClashDatabase();database.Open("recorded-fixture");
            var memory=GC.GetTotalMemory(true);var timer=System.Diagnostics.Stopwatch.StartNew();
            var capture=Capture("recorded-fixture");var version=database.History.BeginScan(capture);
            var stats=new ScanStatistics {ScanId=version.ScanId,Mode=ScanMode.HardAndClearance,Scope=new ScanScope("recorded-fixture")};
            new FullScanDashboardService(database).Complete(Array.Empty<ClashResult>(),rows,stats,10);timer.Stop();long publication=timer.ElapsedMilliseconds;
            Check(database.History.GetObservations(version.ScanId).Count==rows.Count&&publication<10000,"Recorded 1,419-issue fixture publishes atomically within 10-second data-only ceiling");
            timer.Restart();version=database.History.BeginScan(capture);stats.ScanId=version.ScanId;new FullScanDashboardService(database).Complete(database.LoadCurrentClashes(),rows,stats,10);timer.Stop();long rescan=timer.ElapsedMilliseconds;
            timer.Restart();var source=new DashboardDataSource(database,()=>"recorded-fixture",id=>false);var snapshot=source.Read("","");timer.Stop();long comparison=timer.ElapsedMilliseconds;
            Check(snapshot.Rows.Count==rows.Count&&comparison<5000,"Recorded fixture comparison and read projection remain within 5-second ceiling");
            var workspace=new DashboardWorkspace(source,new LifecycleCommandService(database));workspace.Refresh();workspace.QuickFilter="Critical";timer.Restart();var visible=workspace.Visible();timer.Stop();long filter=timer.ElapsedMilliseconds;
            Check(visible.All(r=>DashboardMetricPolicy.Critical(r)&&r.TestType!="Unverified")&&filter<5000,"Recorded fixture filter reconciles evidence policy within 5-second ceiling");
            long retained=GC.GetTotalMemory(true)-memory;Check(retained<256L*1024*1024,"Recorded fixture managed-memory growth stays below 256 MiB synthetic ceiling");
            File.WriteAllText(Path.Combine(root,"recorded-performance.json"),JsonConvert.SerializeObject(new {issues=rows.Count,publicationMs=publication,rescanMs=rescan,comparisonMs=comparison,filterMs=filter,managedBytesDelta=retained,scope="Recorded copied data; excludes native Revit collection/geometry"},Formatting.Indented));
            Console.WriteLine($"Recorded fixture performance: publish {publication} ms; rescan {rescan} ms; compare/read {comparison} ms; filter {filter} ms; managed growth {retained} bytes.");
        } finally {Environment.SetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR",previousDirectory);}
    }

    private static void FailedMigration(string root)
    {
        string path=Path.Combine(root,"bad-legacy-"+Guid.NewGuid().ToString("N")+".db");
        using var c=new SQLiteConnection("Data Source="+path);c.Open();
        Sql(c,"CREATE TABLE Clashes(ClashId TEXT PRIMARY KEY,ElementAId INTEGER,ElementBId INTEGER,MetadataJson TEXT); INSERT INTO Clashes VALUES('a',1,2,'{}'); INSERT INTO Clashes VALUES('b',1,2,'{}');");
        var repository=new ScanHistoryRepository(c,"fixture");
        Reject(()=>repository.Initialize(path,true),"Ambiguous duplicate legacy pair aborts migration instead of merging unrelated state");
        Check(Count(c,"SELECT COUNT(*) FROM Clashes")==2&&Count(c,"SELECT COUNT(*) FROM sqlite_master WHERE name='ScanVersions'")==0,"Failed migration rolls back schema and preserves original rows");
        Check(File.Exists(repository.MigrationBackupPath),"Failed migration retains a usable original backup");
    }
}

namespace ClashResolveAI.Core {public static class Diagnostics {public static void Log(string message,Exception? error=null) {}}}
