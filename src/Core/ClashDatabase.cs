// Core/ClashDatabase.cs  — v4.0
//
// PROFESSIONAL IMPROVEMENT #12: Persistent Clash Database
//
// Clashes no longer live only in session memory.
// This service provides:
//   • SQLite persistence across Revit sessions
//   • Full clash lifecycle tracking
//   • Comments, assignments, revision history
//   • Analytics queries (trends, discipline matrix)
//   • BCF-compatible export data
//
// The database is stored at:
//   %APPDATA%\ClashResolveAI\{ProjectName}.clash.db

using ClashResolveAI.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Dashboard.Persistence;
using ClashResolveAI.ClashEngine;

namespace ClashResolveAI.Core
{
    /// <summary>
    /// SQLite-backed persistent storage for clash results, groups,
    /// lifecycle history, and coordination analytics.
    /// </summary>
    public class ClashDatabase : IDisposable, Dashboard.Application.IGroupCommandStore
    {
        // ── Singleton ─────────────────────────────────────────────────────
        private static ClashDatabase? _instance;
        public  static ClashDatabase   Instance =>
            _instance ?? (_instance = new ClashDatabase());

        private SQLiteConnection? _conn;
        private string            _dbPath = "";
        private SQLiteTransaction? _transaction;
        private bool _publishingScan;
        private ScanHistoryRepository? _history;
        public ScanHistoryRepository History => _history ?? throw new InvalidOperationException("No dashboard database is open.");

        // ════════════════════════════════════════════════════════════════
        //  INIT / OPEN
        // ════════════════════════════════════════════════════════════════

        /// <summary>Open or create database for a given project.</summary>
        public void Open(string projectName)
        {
            try
            {
                _conn?.Dispose();
                _conn = null;
                _history = null;
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ClashResolveAI");
                string verification=Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR")??"";
                if(!string.IsNullOrEmpty(verification))folder=Path.Combine(verification,"databases");
                Directory.CreateDirectory(folder);

                string safeName = string.Join("_",
                    projectName.Split(Path.GetInvalidFileNameChars()));
                _dbPath = Path.Combine(folder, $"{safeName}.clash.db");
                bool existed=File.Exists(_dbPath);
                _conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
                _conn.Open();
                _history=new ScanHistoryRepository(_conn,projectName);
                // Backup and migrate before touching the compatibility schema.
                _history.Initialize(_dbPath,existed);
                CreateSchema();
                _history.RecoverInterruptedAttempts();
                Debug.WriteLine($"[ClashDB] Opened: {_dbPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashDB] Open error: {ex.Message}");
                _conn?.Dispose();_conn = null;_history=null;
                Diagnostics.Log("Database could not open", ex);
                throw;
            }
        }

        private void CreateSchema()
        {
            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS Clashes (
                    ClashId         TEXT PRIMARY KEY,
                    GroupId         TEXT,
                    DetectedAt      TEXT,
                    ElementAId      INTEGER,
                    ElementBId      INTEGER,
                    DisciplineA     TEXT,
                    DisciplineB     TEXT,
                    ClashType       TEXT,
                    Severity        TEXT,
                    Status          TEXT DEFAULT 'New',
                    GapMM           REAL,
                    OverlapVolMM3   REAL,
                    ClashPoint      TEXT,
                    LevelName       TEXT,
                    GridRef         TEXT,
                    ZoneName        TEXT,
                    Priority        TEXT,
                    MovingDisc      TEXT,
                    RuleApplied     TEXT,
                    AiSuggestion    TEXT,
                    RfiText         TEXT,
                    LinkFileA       TEXT,
                    LinkFileB       TEXT,
                    MetadataJson    TEXT,
                    UpdatedAt       TEXT
                );");

            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS ClashGroups (
                    GroupId         TEXT PRIMARY KEY,
                    GroupTitle      TEXT,
                    MaxSeverity     TEXT,
                    Status          TEXT DEFAULT 'New',
                    GroupingReason  TEXT,
                    LevelName       TEXT,
                    ZoneName        TEXT,
                    GridRef         TEXT,
                    DisciplineA     TEXT,
                    DisciplineB     TEXT,
                    PrimaryOffender TEXT,
                    DetectedAt      TEXT,
                    MetadataJson    TEXT
                );");

            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS ClashRevisions (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    ClashId         TEXT,
                    Timestamp       TEXT,
                    Author          TEXT,
                    OldStatus       TEXT,
                    NewStatus       TEXT,
                    Comment         TEXT,
                    FOREIGN KEY(ClashId) REFERENCES Clashes(ClashId)
                );");

            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS WeeklySnapshots (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    WeekStart       TEXT,
                    TotalClashes    INTEGER,
                    Resolved        INTEGER,
                    Critical        INTEGER
                );");

            var columns=new HashSet<string>();
            using(var cmd=_conn!.CreateCommand()){cmd.CommandText="PRAGMA table_info(Clashes)";using var r=cmd.ExecuteReader();while(r.Read())columns.Add(r.GetString(1));}
            foreach(var name in new[]{"Origin","UnverifiedReason","TestType","GeometryEvidence","LinkInstanceA","LinkInstanceB","HostDocumentKey"})
                if(!columns.Contains(name))ExecuteNonQuery("ALTER TABLE Clashes ADD COLUMN "+name+" TEXT");
            if(!columns.Contains("Archived"))ExecuteNonQuery("ALTER TABLE Clashes ADD COLUMN Archived INTEGER NOT NULL DEFAULT 0");
            // Indexes for performance
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_clash_status ON Clashes(Status);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_clash_severity ON Clashes(Severity);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_clash_group ON Clashes(GroupId);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_clash_level ON Clashes(LevelName);");
        }

        // ════════════════════════════════════════════════════════════════
        //  UPSERT CLASH  — insert or update a single clash
        // ════════════════════════════════════════════════════════════════

        public void UpsertClash(ClashResult c)
        {
            if (c == null || c.Origin != ResultOrigin.Full) return;
            if (_conn == null) throw new InvalidOperationException("No dashboard database is open.");
            if(_transaction==null){Atomic(()=>UpsertClash(c));return;}
            try
            {
                string metaJson = JsonConvert.SerializeObject(c.Metadata);

                ExecuteNonQuery(@"
                    INSERT INTO Clashes
                    (ClashId, GroupId, DetectedAt, ElementAId, ElementBId,
                     DisciplineA, DisciplineB, ClashType, Severity, Status,
                     GapMM, OverlapVolMM3, ClashPoint, LevelName, GridRef,
                     ZoneName, Priority, MovingDisc, RuleApplied,
                     AiSuggestion, RfiText, LinkFileA, LinkFileB,
                     MetadataJson, UpdatedAt, TestType, GeometryEvidence, LinkInstanceA, LinkInstanceB, HostDocumentKey, UnverifiedReason, Origin)
                    VALUES
                    (@id, @gid, @det, @eaId, @ebId,
                     @da, @db, @ct, @sev, @status,
                     @gap, @vol, @pt, @lv, @gr,
                     @zone, @pri, @mdisc, @rule,
                     @ai, @rfi, @lfa, @lfb,
                     @meta, @upd, @test, @evidence, @lia, @lib, @host, @reason, @origin)
                    ON CONFLICT(ClashId) DO UPDATE SET
                     GroupId=excluded.GroupId,DetectedAt=excluded.DetectedAt,ElementAId=excluded.ElementAId,ElementBId=excluded.ElementBId,
                     DisciplineA=excluded.DisciplineA,DisciplineB=excluded.DisciplineB,ClashType=excluded.ClashType,Severity=excluded.Severity,Status=excluded.Status,
                     GapMM=excluded.GapMM,OverlapVolMM3=excluded.OverlapVolMM3,ClashPoint=excluded.ClashPoint,LevelName=excluded.LevelName,
                     GridRef=excluded.GridRef,ZoneName=excluded.ZoneName,Priority=excluded.Priority,MovingDisc=excluded.MovingDisc,RuleApplied=excluded.RuleApplied,
                     AiSuggestion=excluded.AiSuggestion,RfiText=excluded.RfiText,LinkFileA=excluded.LinkFileA,LinkFileB=excluded.LinkFileB,
                     MetadataJson=excluded.MetadataJson,UpdatedAt=excluded.UpdatedAt,TestType=excluded.TestType,GeometryEvidence=excluded.GeometryEvidence,
                     LinkInstanceA=excluded.LinkInstanceA,LinkInstanceB=excluded.LinkInstanceB,HostDocumentKey=excluded.HostDocumentKey,
                     UnverifiedReason=excluded.UnverifiedReason,Origin=excluded.Origin,Archived=0",
                    ("@origin",c.Origin.ToString()),
                    ("@reason",c.UnverifiedReason.ToString()),
                    ("@test",c.TestType.ToString()),("@evidence",c.GeometryEvidence),("@lia",c.LinkInstanceA),("@lib",c.LinkInstanceB),("@host",c.HostDocumentKey),
                    ("@id",     c.ClashId),
                    ("@gid",    c.GroupId),
                    ("@det",    c.DetectedAt.ToString("o")),
                    ("@eaId",   c.ElementAId),
                    ("@ebId",   c.ElementBId),
                    ("@da",     c.DisciplineA.ToString()),
                    ("@db",     c.DisciplineB.ToString()),
                    ("@ct",     c.ClashType),
                    ("@sev",    c.Severity.ToString()),
                    ("@status", c.Status.ToString()),
                    ("@gap",    c.GapMM),
                    ("@vol",    c.OverlapVolumeMM3),
                    ("@pt",     string.Format(System.Globalization.CultureInfo.InvariantCulture,"{0:R},{1:R},{2:R}",c.ClashPoint.X,c.ClashPoint.Y,c.ClashPoint.Z)),
                    ("@lv",     c.LevelName),
                    ("@gr",     c.GridRef),
                    ("@zone",   c.ZoneName),
                    ("@pri",    c.Priority),
                    ("@mdisc",  c.MovingDiscipline),
                    ("@rule",   c.RuleApplied),
                    ("@ai",     c.AiSuggestion),
                    ("@rfi",    c.RfiText),
                    ("@lfa",    c.LinkFileA),
                    ("@lfb",    c.LinkFileB),
                    ("@meta",   metaJson),
                    ("@upd",    DateTime.UtcNow.ToString("o")));
                if(!_publishingScan)History.UpsertCurrent(ClashObservationAdapter.Capture(c),_transaction);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Database write failed", ex);
                throw;
            }
        }

        public void DeleteClashes(IEnumerable<string> ids)
        {
            Atomic(()=>{foreach(var id in ids){History.ArchiveCurrent(id,_transaction!);ExecuteNonQuery("UPDATE Clashes SET Archived=1 WHERE ClashId=@id",("@id",id));}});
        }

        public void RestoreLifecycles(IEnumerable<ClashResult> clashes)
        {
            if(_conn==null)return;
            var wanted=clashes.ToDictionary(c=>c.ClashId);
            using var cmd=_conn.CreateCommand();
            cmd.CommandText="SELECT ClashId,Status,MetadataJson,DetectedAt,AiSuggestion,RfiText FROM Clashes";
            using var reader=cmd.ExecuteReader();
            while(reader.Read()) {
                if(!wanted.TryGetValue(reader.GetString(0),out var c))continue;
                if(Enum.TryParse(reader.GetString(1),out ClashStatus status))c.Status=status;
                if(!reader.IsDBNull(2))c.Metadata=JsonConvert.DeserializeObject<ClashMetadata>(reader.GetString(2))??new ClashMetadata();
                if(!reader.IsDBNull(3)&&DateTime.TryParse(reader.GetString(3),System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out var date))c.DetectedAt=date;
                if(!reader.IsDBNull(4))c.AiSuggestion=reader.GetString(4);
                if(!reader.IsDBNull(5))c.RfiText=reader.GetString(5);
            }
        }
        public void RestoreLifecycle(ClashResult clash)
        {
            if (_conn == null) return;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Status, MetadataJson, DetectedAt, AiSuggestion, RfiText FROM Clashes WHERE ClashId=@id";
            cmd.Parameters.AddWithValue("@id", clash.ClashId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return;
            if (Enum.TryParse(reader.GetString(0), out ClashStatus status))
                clash.Status = status;
            if (!reader.IsDBNull(1)) clash.Metadata = JsonConvert.DeserializeObject<ClashMetadata>(reader.GetString(1)) ?? new ClashMetadata();
            if (!reader.IsDBNull(2) && DateTime.TryParse(reader.GetString(2),System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out var first)) clash.DetectedAt = first;
            if (!reader.IsDBNull(3)) clash.AiSuggestion = reader.GetString(3);
            if (!reader.IsDBNull(4)) clash.RfiText = reader.GetString(4);
        }

        /// <summary>Bulk insert clash results (uses a transaction for performance).</summary>
        public void BulkInsertClashes(IEnumerable<ClashResult> clashes)
        {
            Atomic(()=>{foreach(var c in clashes)UpsertClash(c);});
        }

        public List<ClashResult> LoadCurrentClashes() => History.GetCurrent().Select(ClashObservationAdapter.Restore).ToList();
        public void UpdateClashStatuses(IEnumerable<string> ids,ClashStatus status,string author,string comment)
        {new Dashboard.Application.LifecycleCommandService(this).ChangeStatus(ids,status,author,comment);}

        public void CommitFullScan(ScanStatistics stats,IReadOnlyList<ClashResult> rows,IReadOnlyList<ClashObservation> observations,IReadOnlyList<GroupScanRevision> groups)
        {
            if(string.IsNullOrEmpty(stats.ScanId))throw new InvalidOperationException("Full Scan has no persisted attempt identity.");
            Atomic(()=> {
                _publishingScan=true;
                try {
                    History.PrepareGroups(stats.ScanId,groups,observations,_transaction!);
                    var observationMap=observations.ToDictionary(o=>o.ClashId);
                    foreach(var row in rows){row.Metadata=JsonConvert.DeserializeObject<ClashMetadata>(observationMap[row.ClashId].MetadataJson)!;UpsertClash(row);}
                    string completeness=stats.MissingGeometry==0&&stats.BooleanFailures==0&&stats.Unverified==0?"VerifiedWithinScope":"CompletedWithUncertainty";
                    History.CompleteScan(stats.ScanId,observations,groups,JsonConvert.SerializeObject(stats),JsonConvert.SerializeObject(stats.Scope.Snapshot()),completeness,_transaction!);
                } finally {_publishingScan=false;}
            });
        }

        private void Atomic(Action action)
        {
            if(_conn==null)throw new InvalidOperationException("No dashboard database is open.");
            if(_transaction!=null){action();return;}
            using var tx=_conn.BeginTransaction();_transaction=tx;
            try {action();tx.Commit();} catch {tx.Rollback();throw;} finally {_transaction=null;}
        }

        // ════════════════════════════════════════════════════════════════
        //  UPDATE STATUS  — lifecycle transition with revision log
        // ════════════════════════════════════════════════════════════════

        public void UpdateClashStatus(string clashId,ClashStatus newStatus,string author="",string comment="")
        {new Dashboard.Application.LifecycleCommandService(this).ChangeStatus(new[]{clashId},newStatus,author,comment);}

        public ClashObservation? ReadCurrentIssue(string id)=>History.FindCurrent(id);
        public void CommitMutations(IReadOnlyList<IssueMutation> mutations)
        {
            Atomic(()=> {
                foreach(var mutation in mutations) {
                    var current=History.FindCurrent(mutation.Snapshot.ClashId)??throw new InvalidOperationException("Issue is no longer current.");
                    if(current.Status!=mutation.ExpectedStatus||current.MetadataJson!=mutation.ExpectedMetadataJson)throw new InvalidOperationException("Issue changed while the command was being prepared. Refresh and retry.");
                }
                foreach(var mutation in mutations) {
                    var row=mutation.Snapshot;UpsertClash(ClashObservationAdapter.Restore(row));
                    string timestamp=DateTime.UtcNow.ToString("o");
                    History.AppendEvent(new ClashLifecycleEvent {EventId=Guid.NewGuid().ToString("N"),ClashId=row.ClashId,FromStatus=mutation.ExpectedStatus,ToStatus=row.Status,
                        Origin=mutation.Origin,Author=mutation.Author,Timestamp=timestamp,Comment=mutation.Reason},_transaction);
                    if(mutation.ExpectedStatus!=row.Status)ExecuteNonQuery("INSERT INTO ClashRevisions (ClashId,Timestamp,Author,OldStatus,NewStatus,Comment) VALUES(@id,@utc,@author,@old,@new,@reason)",
                        ("@id",row.ClashId),("@utc",timestamp),("@author",mutation.Author),("@old",mutation.ExpectedStatus),("@new",row.Status),("@reason",mutation.Reason));
                }
            });
        }
        public Dashboard.Application.GroupCoordinationState ReadGroupState(string key)=>History.ReadGroupState(key);
        public IReadOnlyList<string> GroupMembers(string scanId,string key)
        {
            var latest=History.GetVersions().Where(v=>v.State==ScanVersionState.Completed).OrderByDescending(v=>v.SequenceNumber).FirstOrDefault();
            if(latest?.ScanId!=scanId)throw new InvalidOperationException("Group changed; select the latest completed scan.");
            return (History.GetGroups(scanId).SingleOrDefault(g=>g.GroupKey==key)??throw new InvalidOperationException("Group is no longer current.")).MemberClashIds;
        }
        public void ChangeGroupStatus(string scanId,string key,ClashStatus target,string author,string reason)
        {Atomic(()=>new Dashboard.Application.LifecycleCommandService(this).ChangeStatus(GroupMembers(scanId,key).Where(id=>ReadCurrentIssue(id)!=null),target,author,reason));}
        public void CommitGroup(string scanId,Dashboard.Application.GroupCoordinationState expected,Dashboard.Application.GroupCoordinationState state,IReadOnlyList<Dashboard.Domain.IssueMutation> changes,string author)
        {
            Atomic(()=>{GroupMembers(scanId,state.GroupKey);if(JsonConvert.SerializeObject(ReadGroupState(state.GroupKey))!=JsonConvert.SerializeObject(expected))throw new InvalidOperationException("Group defaults changed. Refresh and retry.");CommitMutations(changes);if(JsonConvert.SerializeObject(expected)!=JsonConvert.SerializeObject(state))History.SaveGroupState(state,_transaction!,author);});
        }        public void UpsertGroup(ClashGroup g)
        {
            if (_conn == null || g == null) return;
            try
            {
                string metaJson = JsonConvert.SerializeObject(g.Metadata);
                ExecuteNonQuery(@"
                    INSERT INTO ClashGroups
                    (GroupId, GroupTitle, MaxSeverity, Status, GroupingReason,
                     LevelName, ZoneName, GridRef, DisciplineA, DisciplineB,
                     PrimaryOffender, DetectedAt, MetadataJson)
                    VALUES
                    (@gid, @title, @sev, @status, @reason,
                     @lv, @zone, @gr, @da, @db,
                     @off, @det, @meta)
                    ON CONFLICT(GroupId) DO UPDATE SET GroupTitle=excluded.GroupTitle,MaxSeverity=excluded.MaxSeverity,
                     Status=excluded.Status,GroupingReason=excluded.GroupingReason,LevelName=excluded.LevelName,
                     ZoneName=excluded.ZoneName,GridRef=excluded.GridRef,DisciplineA=excluded.DisciplineA,DisciplineB=excluded.DisciplineB,
                     PrimaryOffender=excluded.PrimaryOffender,DetectedAt=excluded.DetectedAt,MetadataJson=excluded.MetadataJson",
                    ("@gid",    g.GroupId),
                    ("@title",  g.GroupTitle),
                    ("@sev",    g.MaxSeverity.ToString()),
                    ("@status", g.Status.ToString()),
                    ("@reason", g.GroupingReason),
                    ("@lv",     g.LevelName),
                    ("@zone",   g.ZoneName),
                    ("@gr",     g.GridRef),
                    ("@da",     g.DisciplineA.ToString()),
                    ("@db",     g.DisciplineB.ToString()),
                    ("@off",    g.PrimaryOffender),
                    ("@det",    g.DetectedAt.ToString("o")),
                    ("@meta",   metaJson));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Database group write failed",ex);throw;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  QUERIES
        // ════════════════════════════════════════════════════════════════

        public int GetClashCount(ClashStatus? status = null)
        {
            if (_conn == null) return 0;
            try
            {
                string sql = status.HasValue
                    ? "SELECT COUNT(*) FROM Clashes WHERE Archived=0 AND Status=@s"
                    : "SELECT COUNT(*) FROM Clashes WHERE Archived=0";
                var result = status.HasValue
                    ? ExecuteScalar(sql, ("@s", status.Value.ToString()))
                    : ExecuteScalar(sql);
                return Convert.ToInt32(result);
            }
            catch { return 0; }
        }

        public int GetGroupCount()
        {
            if (_conn == null) return 0;
            try { return Convert.ToInt32(ExecuteScalar("SELECT COUNT(*) FROM ClashGroups")); }
            catch { return 0; }
        }

        /// <summary>Returns discipline-pair clash counts for the matrix view.</summary>
        public Dictionary<string, int> GetDisciplineMatrix()
        {
            var result = new Dictionary<string, int>();
            if (_conn == null) return result;
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT DisciplineA, DisciplineB, COUNT(*) as cnt
                    FROM Clashes
                    WHERE Archived=0 AND Status NOT IN ('Resolved','Ignored','Closed')
                    GROUP BY DisciplineA, DisciplineB";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string key = $"{reader.GetString(0)} vs {reader.GetString(1)}";
                    result[key] = reader.GetInt32(2);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashDB] DisciplineMatrix error: {ex.Message}");
            }
            return result;
        }

        /// <summary>Weekly trend data for the dashboard chart.</summary>
        public List<WeeklyTrend> GetWeeklyTrends(int weeksBack = 8)
        {
            var result = new List<WeeklyTrend>();
            if (_conn == null) return result;
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT strftime('%Y-W%W', DetectedAt) as week,
                           COUNT(*) as total,
                           SUM(CASE WHEN Status IN ('Resolved','Closed') THEN 1 ELSE 0 END) as res
                    FROM Clashes
                    WHERE Archived=0 AND DetectedAt >= @since
                    GROUP BY week ORDER BY week";
                cmd.Parameters.AddWithValue("@since",
                    DateTime.UtcNow.AddDays(-weeksBack * 7).ToString("o"));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new WeeklyTrend
                    {
                        WeekLabel  = reader.GetString(0),
                        ClashCount = reader.GetInt32(1),
                        Resolved   = reader.GetInt32(2)
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashDB] Trends error: {ex.Message}");
            }
            return result;
        }

        /// <summary>Compatibility numeric accessor; the workspace displays an empty evidence basis as unavailable.</summary>
        public double GetHealthScore()=>_history==null?0:Dashboard.Application.DashboardMetricPolicy.Calculate(_history.GetCurrent(),null,null).Health??0;
        /// <summary>Save weekly snapshot for trend tracking.</summary>
        public void SaveWeeklySnapshot()
        {
            if (_conn == null) return;
            try
            {
                // Check if already saved this week
                string weekStart = GetWeekStart().ToString("o");
                var existing = ExecuteScalar(
                    "SELECT COUNT(*) FROM WeeklySnapshots WHERE WeekStart=@w",
                    ("@w", weekStart));
                if (Convert.ToInt32(existing) > 0) return;

                ExecuteNonQuery(@"
                    INSERT INTO WeeklySnapshots (WeekStart, TotalClashes, Resolved, Critical)
                    VALUES (@w, @t, @r, @c)",
                    ("@w", weekStart),
                    ("@t", GetClashCount()),
                    ("@r", GetClashCount(ClashStatus.Resolved)),
                    ("@c", GetClashCount(ClashStatus.Active)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashDB] Snapshot error: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  PRIVATE HELPERS
        // ════════════════════════════════════════════════════════════════

        private void ExecuteNonQuery(string sql, params (string, object)[] parms)
        {
            if (_conn == null) return;
            using var cmd = _conn.CreateCommand();
            cmd.Transaction=_transaction;
            cmd.CommandText = sql;
            foreach (var (k, v) in parms)
                cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private object? ExecuteScalar(string sql, params (string, object)[] parms)
        {
            if (_conn == null) return null;
            using var cmd = _conn.CreateCommand();
            cmd.Transaction=_transaction;
            cmd.CommandText = sql;
            foreach (var (k, v) in parms)
                cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            return cmd.ExecuteScalar();
        }

        private static DateTime GetWeekStart()
        {
            var today = DateTime.UtcNow.Date;
            int diff = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
            return today.AddDays(-diff);
        }

        public void Dispose()
        {
            _conn?.Close();
            _conn?.Dispose();
            _conn = null;
            _history=null;
        }
    }
}
