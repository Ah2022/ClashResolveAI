using ClashResolveAI.Core;
using ClashResolveAI.Dashboard.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ClashResolveAI.Dashboard.Persistence
{
    public interface IScanHistoryRepository
    {
        ScanVersion BeginScan(ScanCapture capture);
        void EndAttempt(string scanId, ScanVersionState state, string reason);
        IReadOnlyList<ScanVersion> GetVersions();
        IReadOnlyList<ClashObservation> GetObservations(string scanId);
        IReadOnlyList<ClashObservation> GetCurrent();
        IReadOnlyList<ClashLifecycleEvent> GetEvents(string clashId);
    }

    // Uses the same connection as the compatibility current-row store so publication is one transaction.
    public sealed class ScanHistoryRepository : IScanHistoryRepository
    {
        public const int SchemaVersion=2;
        private readonly SQLiteConnection _connection;
        private readonly string _documentKey;
        public string? MigrationBackupPath { get; private set; }
        public ScanHistoryRepository(SQLiteConnection connection,string documentKey)
        { _connection=connection; _documentKey=documentKey; }

        private SQLiteCommand Command(string sql,SQLiteTransaction? tx=null,params (string,object?)[] parameters)
        {
            var cmd=_connection.CreateCommand();cmd.CommandText=sql;cmd.Transaction=tx;
            foreach(var p in parameters)cmd.Parameters.AddWithValue(p.Item1,p.Item2??DBNull.Value);
            return cmd;
        }
        private int Execute(string sql,SQLiteTransaction? tx=null,params (string,object?)[] parameters)
        { using var cmd=Command(sql,tx,parameters);return cmd.ExecuteNonQuery(); }
        private object? Scalar(string sql,SQLiteTransaction? tx=null,params (string,object?)[] parameters)
        { using var cmd=Command(sql,tx,parameters);return cmd.ExecuteScalar(); }
        private static string Json(object value)=>JsonConvert.SerializeObject(value);
        private static string UtcNow()=>DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture);

        public void Initialize(string databasePath,bool existedBeforeOpen)
        {
            Execute("PRAGMA foreign_keys=ON");
            if(Convert.ToInt32(Scalar("PRAGMA foreign_keys"))!=1)throw new InvalidOperationException("SQLite foreign keys must be enabled.");
            bool hasVersion=Convert.ToInt32(Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='DashboardSchema'"))>0;
            int version=hasVersion?Convert.ToInt32(Scalar("SELECT COALESCE(MAX(Version),0) FROM DashboardSchema")):0;
            if(version>SchemaVersion)throw new InvalidOperationException("This dashboard database requires a newer application.");
            if(version==SchemaVersion)return;
            if(existedBeforeOpen) {
                MigrationBackupPath=databasePath+".pre-dashboard-v"+SchemaVersion+"-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff")+".bak";
                using var backup=new SQLiteConnection("Data Source="+MigrationBackupPath+";Version=3;");backup.Open();
                _connection.BackupDatabase(backup,"main","main",-1,null,0);
            }
            using var tx=_connection.BeginTransaction();
            Execute(@"
CREATE TABLE IF NOT EXISTS DashboardSchema (Version INTEGER PRIMARY KEY, AppliedAtUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS DocumentIdentities (DocumentKey TEXT PRIMARY KEY, DisplayName TEXT NOT NULL, IdentityJson TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS DocumentAliases (AliasKey TEXT PRIMARY KEY, DocumentKey TEXT NOT NULL REFERENCES DocumentIdentities(DocumentKey), Reason TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS ScanVersions (
 ScanId TEXT PRIMARY KEY, DocumentKey TEXT NOT NULL REFERENCES DocumentIdentities(DocumentKey),
 SequenceNumber INTEGER NOT NULL, StartedAtUtc TEXT NOT NULL, EndedAtUtc TEXT,
 State TEXT NOT NULL CHECK(State IN ('Running','Completed','Cancelled','Failed','LegacyImported')),
 Completeness TEXT NOT NULL, FailureReason TEXT NOT NULL, CaptureJson TEXT NOT NULL,
 StatisticsJson TEXT NOT NULL, CoverageJson TEXT NOT NULL, UNIQUE(DocumentKey,SequenceNumber));
CREATE TABLE IF NOT EXISTS ClashIdentities (
 ClashId TEXT PRIMARY KEY, NormalizedKey TEXT NOT NULL UNIQUE,
 DocumentKey TEXT NOT NULL REFERENCES DocumentIdentities(DocumentKey),
 FirstSeenScanId TEXT REFERENCES ScanVersions(ScanId), LastSeenScanId TEXT REFERENCES ScanVersions(ScanId),
 CurrentStatus TEXT NOT NULL, AssignedEngineer TEXT NOT NULL, DueDate TEXT,
 CurrentGroupId TEXT NOT NULL, CurrentSnapshotJson TEXT NOT NULL, Archived INTEGER NOT NULL DEFAULT 0,
 LevelName TEXT NOT NULL DEFAULT '', Severity TEXT NOT NULL DEFAULT '', Priority TEXT NOT NULL DEFAULT '', TestType TEXT NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS ClashScanRevisions (
 Id INTEGER PRIMARY KEY AUTOINCREMENT, ClashId TEXT NOT NULL REFERENCES ClashIdentities(ClashId),
 ScanId TEXT NOT NULL REFERENCES ScanVersions(ScanId), ObservationKind TEXT NOT NULL, EvaluationReason TEXT NOT NULL,
 ChangeKind TEXT, ChangeFlags INTEGER NOT NULL DEFAULT 0, SnapshotJson TEXT NOT NULL, UNIQUE(ClashId,ScanId));
CREATE TABLE IF NOT EXISTS ClashLifecycleEvents (
 EventId TEXT PRIMARY KEY, ClashId TEXT NOT NULL REFERENCES ClashIdentities(ClashId), ScanId TEXT REFERENCES ScanVersions(ScanId),
 FromStatus TEXT NOT NULL, ToStatus TEXT NOT NULL, Origin TEXT NOT NULL, Author TEXT NOT NULL,
 Timestamp TEXT NOT NULL, TimestampKind TEXT NOT NULL, Comment TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS GroupCoordinationState (GroupKey TEXT PRIMARY KEY REFERENCES CoordinationGroups(GroupKey),DocumentKey TEXT NOT NULL REFERENCES DocumentIdentities(DocumentKey),SnapshotJson TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS GroupLineage (ScanId TEXT NOT NULL REFERENCES ScanVersions(ScanId),ParentKey TEXT NOT NULL REFERENCES CoordinationGroups(GroupKey),ChildKey TEXT NOT NULL REFERENCES CoordinationGroups(GroupKey),Kind TEXT NOT NULL,PRIMARY KEY(ScanId,ParentKey,ChildKey));
CREATE TABLE IF NOT EXISTS GroupCoordinationEvents (EventId TEXT PRIMARY KEY,GroupKey TEXT NOT NULL REFERENCES CoordinationGroups(GroupKey),TimestampUtc TEXT NOT NULL,Author TEXT NOT NULL,SnapshotJson TEXT NOT NULL);
CREATE TRIGGER IF NOT EXISTS immutable_group_event_update BEFORE UPDATE ON GroupCoordinationEvents BEGIN SELECT RAISE(ABORT,'Group event immutable'); END;
CREATE TRIGGER IF NOT EXISTS immutable_group_event_delete BEFORE DELETE ON GroupCoordinationEvents BEGIN SELECT RAISE(ABORT,'Group event immutable'); END;
CREATE TRIGGER IF NOT EXISTS immutable_lineage_update BEFORE UPDATE ON GroupLineage BEGIN SELECT RAISE(ABORT,'Lineage immutable'); END;
CREATE TRIGGER IF NOT EXISTS immutable_lineage_delete BEFORE DELETE ON GroupLineage BEGIN SELECT RAISE(ABORT,'Lineage immutable'); END;
CREATE TABLE IF NOT EXISTS CoordinationGroups (GroupKey TEXT PRIMARY KEY, IdentityVersion TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS GroupScanRevisions (
 GroupKey TEXT NOT NULL REFERENCES CoordinationGroups(GroupKey), ScanId TEXT NOT NULL REFERENCES ScanVersions(ScanId),
 SnapshotJson TEXT NOT NULL, PRIMARY KEY(GroupKey,ScanId));
CREATE TABLE IF NOT EXISTS GroupScanMembers (
 GroupKey TEXT NOT NULL, ScanId TEXT NOT NULL, ClashId TEXT NOT NULL,
 PRIMARY KEY(GroupKey,ScanId,ClashId),
 FOREIGN KEY(GroupKey,ScanId) REFERENCES GroupScanRevisions(GroupKey,ScanId),
 FOREIGN KEY(ClashId,ScanId) REFERENCES ClashScanRevisions(ClashId,ScanId));
CREATE INDEX IF NOT EXISTS ix_scan_document_state ON ScanVersions(DocumentKey,State,SequenceNumber);
CREATE INDEX IF NOT EXISTS ix_revision_scan ON ClashScanRevisions(ScanId);
CREATE INDEX IF NOT EXISTS ix_identity_document_status ON ClashIdentities(DocumentKey,Archived,CurrentStatus);
CREATE INDEX IF NOT EXISTS ix_identity_owner ON ClashIdentities(DocumentKey,AssignedEngineer);
CREATE INDEX IF NOT EXISTS ix_identity_level ON ClashIdentities(DocumentKey,LevelName);
CREATE INDEX IF NOT EXISTS ix_event_issue_time ON ClashLifecycleEvents(ClashId,Timestamp);
CREATE TRIGGER IF NOT EXISTS immutable_completed_scan_update BEFORE UPDATE ON ScanVersions
 WHEN OLD.State IN ('Completed','LegacyImported') BEGIN SELECT RAISE(ABORT,'Completed scan is immutable'); END;
CREATE TRIGGER IF NOT EXISTS immutable_completed_scan_delete BEFORE DELETE ON ScanVersions
 WHEN OLD.State IN ('Completed','LegacyImported') BEGIN SELECT RAISE(ABORT,'Completed scan is immutable'); END;
CREATE TRIGGER IF NOT EXISTS immutable_observation_update BEFORE UPDATE ON ClashScanRevisions BEGIN SELECT RAISE(ABORT,'Observation is immutable'); END;
CREATE TRIGGER IF NOT EXISTS immutable_observation_delete BEFORE DELETE ON ClashScanRevisions BEGIN SELECT RAISE(ABORT,'Observation is immutable'); END;
CREATE TRIGGER IF NOT EXISTS observation_only_running BEFORE INSERT ON ClashScanRevisions
 WHEN (SELECT State FROM ScanVersions WHERE ScanId=NEW.ScanId)!='Running' BEGIN SELECT RAISE(ABORT,'Scan is not running'); END;
CREATE TRIGGER IF NOT EXISTS immutable_group_update BEFORE UPDATE ON GroupScanRevisions BEGIN SELECT RAISE(ABORT,'Group snapshot is immutable'); END;
CREATE TRIGGER IF NOT EXISTS immutable_group_delete BEFORE DELETE ON GroupScanRevisions BEGIN SELECT RAISE(ABORT,'Group snapshot is immutable'); END;
CREATE TRIGGER IF NOT EXISTS group_only_running BEFORE INSERT ON GroupScanRevisions
 WHEN (SELECT State FROM ScanVersions WHERE ScanId=NEW.ScanId)!='Running' BEGIN SELECT RAISE(ABORT,'Scan is not running'); END;
CREATE TRIGGER IF NOT EXISTS immutable_member_update BEFORE UPDATE ON GroupScanMembers BEGIN SELECT RAISE(ABORT,'Membership is immutable'); END;
CREATE TRIGGER IF NOT EXISTS immutable_member_delete BEFORE DELETE ON GroupScanMembers BEGIN SELECT RAISE(ABORT,'Membership is immutable'); END;
CREATE TRIGGER IF NOT EXISTS member_only_running BEFORE INSERT ON GroupScanMembers
 WHEN (SELECT State FROM ScanVersions WHERE ScanId=NEW.ScanId)!='Running' BEGIN SELECT RAISE(ABORT,'Scan is not running'); END;
CREATE TRIGGER IF NOT EXISTS immutable_event_update BEFORE UPDATE ON ClashLifecycleEvents BEGIN SELECT RAISE(ABORT,'Event is immutable'); END;
CREATE TRIGGER IF NOT EXISTS immutable_event_delete BEFORE DELETE ON ClashLifecycleEvents BEGIN SELECT RAISE(ABORT,'Event is immutable'); END;
",tx);
            Execute("INSERT INTO DocumentIdentities VALUES(@key,'','{}') ON CONFLICT(DocumentKey) DO NOTHING",tx,("@key",_documentKey));
            if(version==0)ImportLegacy(tx);
            Execute("INSERT INTO DashboardSchema VALUES(@version,@utc)",tx,("@version",SchemaVersion),("@utc",UtcNow()));
            tx.Commit();
        }

        public void RecoverInterruptedAttempts()
        {
            Execute("UPDATE ScanVersions SET State='Failed',EndedAtUtc=@now,Completeness='Incomplete',FailureReason='Application ended before scan publication' WHERE DocumentKey=@doc AND State='Running'",null,("@now",UtcNow()),("@doc",_documentKey));
        }

        public ScanVersion BeginScan(ScanCapture capture)
        {
            if(capture.DocumentKey!=_documentKey)throw new InvalidOperationException("Scan document does not match repository.");
            using var tx=_connection.BeginTransaction();
            Execute("UPDATE DocumentIdentities SET DisplayName=@name,IdentityJson=@identity WHERE DocumentKey=@doc",tx,("@name",capture.DisplayName),("@identity",capture.DocumentIdentityJson),("@doc",_documentKey));
            var version=new ScanVersion { ScanId=Guid.NewGuid().ToString("N"),Capture=JsonConvert.DeserializeObject<ScanCapture>(Json(capture))!,
                SequenceNumber=Convert.ToInt32(Scalar("SELECT COALESCE(MAX(SequenceNumber),0)+1 FROM ScanVersions WHERE DocumentKey=@doc",tx,("@doc",_documentKey))),
                StartedAtUtc=DateTime.UtcNow,State=ScanVersionState.Running };
            InsertVersion(version,tx);tx.Commit();return version;
        }

        private void InsertVersion(ScanVersion v,SQLiteTransaction tx)
        {
            Execute("INSERT INTO ScanVersions VALUES(@id,@doc,@seq,@start,NULL,'Running','Pending','',@capture,'{}','{}')",tx,
                ("@id",v.ScanId),("@doc",_documentKey),("@seq",v.SequenceNumber),("@start",v.StartedAtUtc.ToString("o")),("@capture",Json(v.Capture)));
        }

        public void EndAttempt(string scanId,ScanVersionState state,string reason)
        {
            if(state!=ScanVersionState.Failed&&state!=ScanVersionState.Cancelled)throw new ArgumentException("Only failed or cancelled attempts can be ended here.");
            Execute("UPDATE ScanVersions SET State=@state,EndedAtUtc=@utc,Completeness='Incomplete',FailureReason=@reason WHERE ScanId=@id AND DocumentKey=@doc AND State='Running'",null,
                ("@state",state.ToString()),("@utc",UtcNow()),("@reason",reason),("@id",scanId),("@doc",_documentKey));
        }

        public Application.GroupCoordinationState ReadGroupState(string key)
        {
            var state=ReadJson<Application.GroupCoordinationState>("SELECT SnapshotJson FROM GroupCoordinationState WHERE GroupKey=@key AND DocumentKey=@doc",("@key",key),("@doc",_documentKey)).SingleOrDefault();
            if(state!=null)return state;
            var saved=ReadJson<GroupScanRevision>("SELECT g.SnapshotJson FROM GroupScanRevisions g JOIN ScanVersions s ON s.ScanId=g.ScanId WHERE g.GroupKey=@key AND s.DocumentKey=@doc ORDER BY s.SequenceNumber DESC LIMIT 1",("@key",key),("@doc",_documentKey)).SingleOrDefault();
            if(saved!=null){var captured=JsonConvert.DeserializeObject<Application.GroupCoordinationState>(saved.CoordinationJson);if(captured!=null&&captured.GroupKey!="")return captured;var metadata=JObject.Parse(saved.MetadataJson);return new Application.GroupCoordinationState {GroupKey=key,Owner=(string?)metadata["AssignedEngineer"]??"",DueUtc=metadata["DueDate"]?.ToObject<DateTime?>()?.ToUniversalTime()};}
            return new Application.GroupCoordinationState {GroupKey=key};
        }        public void SaveGroupState(Application.GroupCoordinationState state,SQLiteTransaction tx,string author)
        {
            Execute("INSERT INTO GroupCoordinationState VALUES(@key,@doc,@json) ON CONFLICT(GroupKey) DO UPDATE SET SnapshotJson=excluded.SnapshotJson",tx,("@key",state.GroupKey),("@doc",_documentKey),("@json",Json(state)));
            Execute("INSERT INTO GroupCoordinationEvents VALUES(@id,@key,@utc,@author,@json)",tx,("@id",Guid.NewGuid().ToString("N")),("@key",state.GroupKey),("@utc",UtcNow()),("@author",author),("@json",Json(state)));
        }
        public IReadOnlyList<Application.GroupLineage> GetLineage(string scanId)
        {
            var result=new List<Application.GroupLineage>();using var cmd=Command("SELECT ParentKey,ChildKey,Kind FROM GroupLineage WHERE ScanId=@scan AND EXISTS(SELECT 1 FROM ScanVersions WHERE ScanId=@scan AND DocumentKey=@doc)",null,("@scan",scanId),("@doc",_documentKey));using var r=cmd.ExecuteReader();while(r.Read())result.Add(new Application.GroupLineage {Parent=r.GetString(0),Child=r.GetString(1),Kind=r.GetString(2)});return result;
        }
        public void PrepareGroups(string scanId,IReadOnlyList<GroupScanRevision> groups,IReadOnlyList<ClashObservation> observations,SQLiteTransaction tx)
        {
            var previous=GetVersions().Where(v=>v.State==ScanVersionState.Completed||v.State==ScanVersionState.LegacyImported).OrderByDescending(v=>v.SequenceNumber).FirstOrDefault();
            var parents=previous==null?Array.Empty<GroupScanRevision>():GetGroups(previous.ScanId);
            var links=Application.GroupCoordinationService.Relate(parents,groups);
            var parentsByChild=links.GroupBy(l=>l.Child).ToDictionary(g=>g.Key,g=>g.Select(l=>l.Parent).ToList());
            var parentStates=links.Select(l=>l.Parent).Distinct().ToDictionary(key=>key,ReadGroupState);
            var observationMap=observations.ToDictionary(o=>o.ClashId);
            foreach(var group in groups){
                Execute("INSERT INTO CoordinationGroups VALUES(@key,'membership-v1') ON CONFLICT(GroupKey) DO NOTHING",tx,("@key",group.GroupKey));
                var state=ReadGroupState(group.GroupKey);
                if(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM GroupCoordinationState WHERE GroupKey=@key",tx,("@key",group.GroupKey)))==0){state=Application.GroupCoordinationService.Inherit(group.GroupKey,parentsByChild.TryGetValue(group.GroupKey,out var parentKeys)?parentKeys.Select(parent=>parentStates[parent]):Array.Empty<Application.GroupCoordinationState>());SaveGroupState(state,tx,"Scan");}
                group.CoordinationJson=Json(state);
                var statuses=group.MemberClashIds.Select(id=>observationMap[id].Status).Distinct().ToList();group.Status=statuses.Count==1?statuses[0]:"Mixed";
                foreach(var row in group.MemberClashIds.Select(id=>observationMap[id])){var metadata=JObject.Parse(row.MetadataJson);if(state.Note=="")Application.GroupCoordinationService.ApplyDefault(metadata,group.GroupKey,state.Owner,state.DueUtc);row.MetadataJson=metadata.ToString(Formatting.None);}
            }
            foreach(var link in links)Execute("INSERT INTO GroupLineage VALUES(@scan,@parent,@child,@kind)",tx,("@scan",scanId),("@parent",link.Parent),("@child",link.Child),("@kind",link.Kind));
        }
        public void CompleteScan(string scanId,IReadOnlyList<ClashObservation> observations,IReadOnlyList<GroupScanRevision> groups,
            string statisticsJson,string coverageJson,string completeness,SQLiteTransaction tx)
        {
            if((Scalar("SELECT State FROM ScanVersions WHERE ScanId=@id AND DocumentKey=@doc",tx,("@id",scanId),("@doc",_documentKey)) as string)!="Running")
                throw new InvalidOperationException("Only a running scan can be completed.");
            foreach(var observation in observations) {
                var old=Scalar("SELECT CurrentStatus FROM ClashIdentities WHERE ClashId=@id",tx,("@id",observation.ClashId)) as string;
                UpsertCurrent(observation,tx,observation.ObservationKind==ScanObservationKind.Observed?scanId:null);
                InsertObservation(observation,scanId,tx);
                if(old!=observation.Status)AppendEvent(new ClashLifecycleEvent {
                    EventId=Guid.NewGuid().ToString("N"),ClashId=observation.ClashId,ScanId=scanId,
                    FromStatus=old??"",ToStatus=observation.Status,Origin="Scan",Timestamp=UtcNow(),Comment=observation.EvaluationReason
                },tx);
            }
            foreach(var group in groups) {
                Execute("INSERT INTO CoordinationGroups VALUES(@key,'membership-v1') ON CONFLICT(GroupKey) DO NOTHING",tx,("@key",group.GroupKey));
                Execute("INSERT INTO GroupScanRevisions VALUES(@key,@scan,@json)",tx,("@key",group.GroupKey),("@scan",scanId),("@json",Json(group)));
                foreach(var member in group.MemberClashIds)Execute("INSERT INTO GroupScanMembers VALUES(@key,@scan,@issue)",tx,("@key",group.GroupKey),("@scan",scanId),("@issue",member));
            }
            Execute("UPDATE ScanVersions SET State='Completed',EndedAtUtc=@utc,Completeness=@complete,StatisticsJson=@stats,CoverageJson=@coverage WHERE ScanId=@id",tx,
                ("@utc",UtcNow()),("@complete",completeness),("@stats",statisticsJson),("@coverage",coverageJson),("@id",scanId));
        }

        public void UpsertCurrent(ClashObservation c,SQLiteTransaction? tx=null,string? observedScanId=null)
        {
            if(c.DocumentKey!=_documentKey||string.IsNullOrEmpty(c.ClashId)||string.IsNullOrEmpty(c.NormalizedKey))throw new InvalidOperationException("Invalid issue identity for this document.");
            var existingKey=Scalar("SELECT NormalizedKey FROM ClashIdentities WHERE ClashId=@id",tx,("@id",c.ClashId)) as string;
            if(existingKey!=null&&existingKey!=c.NormalizedKey)throw new InvalidOperationException("An issue identity cannot be reassigned to a different pair.");
            var metadata=JObject.Parse(c.MetadataJson);
            Execute(@"INSERT INTO ClashIdentities
(ClashId,NormalizedKey,DocumentKey,FirstSeenScanId,LastSeenScanId,CurrentStatus,AssignedEngineer,DueDate,CurrentGroupId,CurrentSnapshotJson,Archived,LevelName,Severity,Priority,TestType)
VALUES(@id,@key,@doc,@scan,@scan,@status,@owner,@due,@group,@json,0,@level,@severity,@priority,@test)
ON CONFLICT(ClashId) DO UPDATE SET
 FirstSeenScanId=COALESCE(ClashIdentities.FirstSeenScanId,excluded.FirstSeenScanId),
 LastSeenScanId=COALESCE(excluded.LastSeenScanId,ClashIdentities.LastSeenScanId),
 CurrentStatus=excluded.CurrentStatus,AssignedEngineer=excluded.AssignedEngineer,DueDate=excluded.DueDate,
 CurrentGroupId=excluded.CurrentGroupId,CurrentSnapshotJson=excluded.CurrentSnapshotJson,Archived=0,
 LevelName=excluded.LevelName,Severity=excluded.Severity,Priority=excluded.Priority,TestType=excluded.TestType",tx,
                ("@id",c.ClashId),("@key",c.NormalizedKey),("@doc",c.DocumentKey),("@scan",observedScanId),("@status",c.Status),
                ("@owner",(string?)metadata["AssignedEngineer"]??""),("@due",(string?)metadata["DueDate"]),("@group",c.GroupId),("@json",Json(c)),
                ("@level",c.LevelName),("@severity",c.Severity),("@priority",c.Priority),("@test",c.TestType));
        }
        private void InsertObservation(ClashObservation c,string scanId,SQLiteTransaction tx)
        {
            Execute("INSERT INTO ClashScanRevisions (ClashId,ScanId,ObservationKind,EvaluationReason,ChangeKind,ChangeFlags,SnapshotJson) VALUES(@id,@scan,@kind,@reason,@change,@flags,@json)",tx,
                ("@id",c.ClashId),("@scan",scanId),("@kind",c.ObservationKind.ToString()),("@reason",c.EvaluationReason),("@change",c.ChangeKind?.ToString()),("@flags",(int)c.ChangeFlags),("@json",Json(c)));
        }
        public void AppendEvent(ClashLifecycleEvent e,SQLiteTransaction? tx=null)
        {
            if(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM ClashIdentities WHERE ClashId=@id AND DocumentKey=@doc",tx,("@id",e.ClashId),("@doc",_documentKey)))!=1)
                throw new InvalidOperationException("Event issue does not belong to this document.");
            if(e.ScanId!=null&&Convert.ToInt32(Scalar("SELECT COUNT(*) FROM ScanVersions WHERE ScanId=@id AND DocumentKey=@doc",tx,("@id",e.ScanId),("@doc",_documentKey)))!=1)
                throw new InvalidOperationException("Event scan does not belong to this document.");
            Execute("INSERT INTO ClashLifecycleEvents VALUES(@id,@issue,@scan,@from,@to,@origin,@author,@time,@kind,@comment)",tx,
                ("@id",e.EventId),("@issue",e.ClashId),("@scan",e.ScanId),("@from",e.FromStatus),("@to",e.ToStatus),
                ("@origin",e.Origin),("@author",e.Author),("@time",e.Timestamp),("@kind",e.TimestampKind),("@comment",e.Comment));
        }
        public void ArchiveCurrent(string clashId,SQLiteTransaction tx)
        { Execute("UPDATE ClashIdentities SET Archived=1 WHERE ClashId=@id AND DocumentKey=@doc",tx,("@id",clashId),("@doc",_documentKey)); }

        public IReadOnlyList<ScanVersion> GetVersions()
        {
            var rows=new List<ScanVersion>();
            using var cmd=Command("SELECT ScanId,SequenceNumber,StartedAtUtc,EndedAtUtc,State,Completeness,FailureReason,CaptureJson,StatisticsJson,CoverageJson FROM ScanVersions WHERE DocumentKey=@doc ORDER BY SequenceNumber",null,("@doc",_documentKey));
            using var reader=cmd.ExecuteReader();
            while(reader.Read())rows.Add(new ScanVersion {
                ScanId=reader.GetString(0),SequenceNumber=reader.GetInt32(1),StartedAtUtc=DateTime.Parse(reader.GetString(2),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),
                EndedAtUtc=reader.IsDBNull(3)?(DateTime?)null:DateTime.Parse(reader.GetString(3),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),
                State=(ScanVersionState)Enum.Parse(typeof(ScanVersionState),reader.GetString(4)),Completeness=reader.GetString(5),FailureReason=reader.GetString(6),
                Capture=JsonConvert.DeserializeObject<ScanCapture>(reader.GetString(7))!,StatisticsJson=reader.GetString(8),CoverageJson=reader.GetString(9)
            });
            return rows;
        }
        public IReadOnlyList<ClashObservation> GetObservations(string scanId)=>ReadJson<ClashObservation>(
            "SELECT r.SnapshotJson FROM ClashScanRevisions r JOIN ScanVersions s ON s.ScanId=r.ScanId WHERE r.ScanId=@scan AND s.DocumentKey=@doc ORDER BY r.ClashId",("@scan",scanId),("@doc",_documentKey));
        public IReadOnlyList<IssueScanRecord> GetIssueHistory(string clashId)
        {
            var rows=new List<IssueScanRecord>();
            using var command=Command("SELECT r.ScanId,r.SnapshotJson FROM ClashScanRevisions r JOIN ScanVersions s ON s.ScanId=r.ScanId WHERE r.ClashId=@id AND s.DocumentKey=@doc AND s.State='Completed' ORDER BY s.SequenceNumber",null,("@id",clashId),("@doc",_documentKey));
            using var reader=command.ExecuteReader();
            while(reader.Read())rows.Add(new IssueScanRecord {ScanId=reader.GetString(0),Observation=JsonConvert.DeserializeObject<ClashObservation>(reader.GetString(1))!});
            return rows;
        }
        public IReadOnlyList<ClashObservation> GetCurrent()=>ReadJson<ClashObservation>(
            "SELECT CurrentSnapshotJson FROM ClashIdentities WHERE DocumentKey=@doc AND Archived=0 ORDER BY ClashId",("@doc",_documentKey));
        public IReadOnlyList<ClashObservation> GetKnown()
        {
            var rows=ReadJson<ClashObservation>("SELECT CurrentSnapshotJson FROM ClashIdentities WHERE DocumentKey=@doc ORDER BY ClashId",("@doc",_documentKey));
            var hydrate=rows.Where(c=>c.SeenInScanCount==0).ToDictionary(c=>c.ClashId);
            if(hydrate.Count==0)return rows;
            // Read projection for Phase 1 snapshots; immutable historical records are never rewritten.
            var absent=new HashSet<string>();
            using var cmd=Command("SELECT r.ClashId,r.ObservationKind,s.EndedAtUtc,r.SnapshotJson FROM ClashScanRevisions r JOIN ScanVersions s ON s.ScanId=r.ScanId WHERE s.DocumentKey=@doc AND s.State='Completed' ORDER BY s.SequenceNumber",null,("@doc",_documentKey));
            using var reader=cmd.ExecuteReader();while(reader.Read()) {
                if(!hydrate.TryGetValue(reader.GetString(0),out var row))continue;
                var utc=DateTime.Parse(reader.GetString(2),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
                if(reader.GetString(1)=="Observed") {
                    row.SeenInScanCount++;row.ConsecutiveScanCount++;row.LastSeenAtUtc=utc;
                    if(absent.Remove(row.ClashId)&&JsonConvert.DeserializeObject<ClashObservation>(reader.GetString(3))!.TestType!="Unverified"){row.ReopenedAtUtc=utc;row.OpenEpisodeAtUtc=utc;}
                    row.OpenEpisodeAtUtc=row.OpenEpisodeAtUtc??(row.DetectedAt.Kind==DateTimeKind.Utc?row.DetectedAt:utc);
                }
                else if(reader.GetString(1)=="VerifiedAbsent"){row.ConsecutiveScanCount=0;if(absent.Add(row.ClashId))row.ResolvedAtUtc=utc;}
            }
            return rows;
        }
        public ClashObservation? FindCurrent(string clashId)=>ReadJson<ClashObservation>(
            "SELECT CurrentSnapshotJson FROM ClashIdentities WHERE DocumentKey=@doc AND ClashId=@id AND Archived=0",("@doc",_documentKey),("@id",clashId)).SingleOrDefault();
        public IReadOnlyList<GroupScanRevision> GetGroups(string scanId)=>ReadJson<GroupScanRevision>(
            "SELECT g.SnapshotJson FROM GroupScanRevisions g JOIN ScanVersions s ON s.ScanId=g.ScanId WHERE g.ScanId=@scan AND s.DocumentKey=@doc ORDER BY g.GroupKey",("@scan",scanId),("@doc",_documentKey));
        private List<T> ReadJson<T>(string sql,params (string,object?)[] parameters)
        {
            var rows=new List<T>();using var cmd=Command(sql,null,parameters);using var reader=cmd.ExecuteReader();
            while(reader.Read())rows.Add(JsonConvert.DeserializeObject<T>(reader.GetString(0))!);return rows;
        }
        public IReadOnlyList<ClashLifecycleEvent> GetDocumentEvents()=>ReadEvents(null);
        public IReadOnlyList<ClashLifecycleEvent> GetEvents(string clashId)=>ReadEvents(clashId);
        private IReadOnlyList<ClashLifecycleEvent> ReadEvents(string? clashId)
        {
            var rows=new List<ClashLifecycleEvent>();
            using var cmd=Command("SELECT e.* FROM ClashLifecycleEvents e JOIN ClashIdentities c ON c.ClashId=e.ClashId WHERE (@id IS NULL OR e.ClashId=@id) AND c.DocumentKey=@doc ORDER BY e.Timestamp,e.EventId",null,("@id",clashId),("@doc",_documentKey));
            using var r=cmd.ExecuteReader();while(r.Read())rows.Add(new ClashLifecycleEvent {
                EventId=r.GetString(0),ClashId=r.GetString(1),ScanId=r.IsDBNull(2)?null:r.GetString(2),FromStatus=r.GetString(3),ToStatus=r.GetString(4),Origin=r.GetString(5),Author=r.GetString(6),Timestamp=r.GetString(7),TimestampKind=r.GetString(8),Comment=r.GetString(9)
            });return rows;
        }

        private void ImportLegacy(SQLiteTransaction tx)
        {
            if(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Clashes'",tx))==0)return;
            var rows=new List<ClashObservation>();
            using(var cmd=Command("SELECT * FROM Clashes",tx))using(var reader=cmd.ExecuteReader())while(reader.Read()) {
                var fields=new Dictionary<string,object>();for(int i=0;i<reader.FieldCount;i++)if(!reader.IsDBNull(i))fields[reader.GetName(i)]=reader.GetValue(i);
                string S(string name,string fallback="")=>fields.TryGetValue(name,out var value)?Convert.ToString(value,CultureInfo.InvariantCulture)??fallback:fallback;
                long L(string name)=>fields.TryGetValue(name,out var value)?Convert.ToInt64(value):0;
                double D(string name)=>fields.TryGetValue(name,out var value)?Convert.ToDouble(value):0;
                if(S("Origin","Full")!="Full")continue;
                if(!string.IsNullOrEmpty(S("HostDocumentKey"))&&S("HostDocumentKey")!=_documentKey)throw new InvalidDataException("Legacy database contains a different document; migration requires an explicit identity mapping.");
                var point=S("ClashPoint").Split(',');
                double P(int i)=>point.Length==3&&double.TryParse(point[i],NumberStyles.Float,CultureInfo.InvariantCulture,out var value)?value:0;
                rows.Add(new ClashObservation {
                    ClashId=S("ClashId"),DocumentKey=_documentKey,NormalizedKey=ClashIdentity.Pair(_documentKey,L("ElementAId"),S("LinkInstanceA"),L("ElementBId"),S("LinkInstanceB")),
                    GroupId=S("GroupId"),ElementAId=L("ElementAId"),ElementBId=L("ElementBId"),LinkInstanceA=S("LinkInstanceA"),LinkInstanceB=S("LinkInstanceB"),
                    LinkFileA=S("LinkFileA"),LinkFileB=S("LinkFileB"),DisciplineA=S("DisciplineA","Unknown"),DisciplineB=S("DisciplineB","Unknown"),
                    TestType=S("TestType","Unverified"),ClashType=S("ClashType"),Severity=S("Severity"),Status=S("Status","New"),Priority=S("Priority"),
                    GapMm=D("GapMM"),OverlapVolumeMm3=D("OverlapVolMM3"),X=P(0),Y=P(1),Z=P(2),LevelName=S("LevelName"),GridRef=S("GridRef"),ZoneName=S("ZoneName"),
                    GeometryEvidence=S("GeometryEvidence"),UnverifiedReason=S("UnverifiedReason"),RuleApplied=S("RuleApplied"),MovingDiscipline=S("MovingDisc"),
                    MetadataJson=S("MetadataJson","{}"),AiSuggestion=S("AiSuggestion"),RfiText=S("RfiText"),
                    DetectedAt=DateTime.TryParse(S("DetectedAt"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var detected)?detected:DateTime.MinValue,
                    DetectedAtOriginalText=S("DetectedAt"),TimestampKind="LegacyLocalOrUnknown",ObservationKind=ScanObservationKind.LegacyImported,EvaluationReason="Imported current row; previous geometry history is unavailable"
                });
            }
            if(rows.Count==0)return;
            var legacy=new ScanVersion {ScanId="legacy-"+Guid.NewGuid().ToString("N"),SequenceNumber=0,StartedAtUtc=DateTime.UtcNow,Capture=new ScanCapture {DocumentKey=_documentKey,IdentityVersion="pair-v1",ScanMode="LegacyUnknown"}};
            InsertVersion(legacy,tx);
            foreach(var row in rows){UpsertCurrent(row,tx);InsertObservation(row,legacy.ScanId,tx);}
            // Preserve any saved legacy group metadata without inventing cross-scan coordination identity.
            if(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ClashGroups'",tx))>0) {
                var importedGroups=new List<GroupScanRevision>();
                using(var cmd=Command("SELECT * FROM ClashGroups",tx))using(var r=cmd.ExecuteReader())while(r.Read()) {
                    var fields=new Dictionary<string,string>();for(int i=0;i<r.FieldCount;i++)if(!r.IsDBNull(i))fields[r.GetName(i)]=Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)??"";
                    string S(string name,string fallback="")=>fields.TryGetValue(name,out var value)?value:fallback;
                    var members=rows.Where(c=>c.GroupId==S("GroupId")).Select(c=>c.ClashId).OrderBy(id=>id,StringComparer.Ordinal).ToList();
                    if(members.Count==0)continue;
                    importedGroups.Add(new GroupScanRevision {GroupKey=HistoryHash.Of("legacy-group|"+_documentKey+"|"+S("GroupId")),
                        Title=S("GroupTitle"),Reason=S("GroupingReason"),PrimaryOffender=S("PrimaryOffender"),Status=S("Status"),MaxSeverity=S("MaxSeverity"),
                        LevelName=S("LevelName"),GridRef=S("GridRef"),ZoneName=S("ZoneName"),DisciplineA=S("DisciplineA"),DisciplineB=S("DisciplineB"),
                        MetadataJson=S("MetadataJson","{}"),MemberClashIds=members});
                }
                foreach(var group in importedGroups) {
                    Execute("INSERT INTO CoordinationGroups VALUES(@key,'legacy-group-v1')",tx,("@key",group.GroupKey));
                    Execute("INSERT INTO GroupScanRevisions VALUES(@key,@scan,@json)",tx,("@key",group.GroupKey),("@scan",legacy.ScanId),("@json",Json(group)));
                    foreach(var member in group.MemberClashIds)Execute("INSERT INTO GroupScanMembers VALUES(@key,@scan,@issue)",tx,("@key",group.GroupKey),("@scan",legacy.ScanId),("@issue",member));
                }
            }
            var known=new HashSet<string>(rows.Select(r=>r.ClashId));
            if(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ClashRevisions'",tx))>0) {
                var events=new List<ClashLifecycleEvent>();
                using(var cmd=Command("SELECT Id,ClashId,Timestamp,Author,OldStatus,NewStatus,Comment FROM ClashRevisions",tx))using(var r=cmd.ExecuteReader())while(r.Read()) {
                    string S(int i)=>r.IsDBNull(i)?"":r.GetValue(i).ToString()!;
                    if(known.Contains(S(1)))events.Add(new ClashLifecycleEvent {EventId="legacy-"+S(0),ClashId=S(1),Timestamp=S(2),Author=S(3),FromStatus=S(4),ToStatus=S(5),Comment=S(6),Origin="LegacyUser",TimestampKind="LegacyLocalOrUnknown"});
                }
                foreach(var e in events)AppendEvent(e,tx);
            }
            Execute("UPDATE ScanVersions SET State='LegacyImported',EndedAtUtc=@utc,Completeness='LegacyUnknown' WHERE ScanId=@id",tx,("@utc",UtcNow()),("@id",legacy.ScanId));
        }
    }
}
