using System;
using System.Linq;
using ClashResolveAI.Core;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Dashboard.Application;

namespace ClashResolveAI.Dashboard.Persistence
{
    internal sealed class DashboardDataSource : IDashboardDataSource, IDashboardHistorySource, IDashboardAnalyticsSource
    {
        private readonly ClashDatabase _database;
        private readonly Func<string> _document;
        private readonly Func<string,bool> _stale;
        private readonly Func<bool> _requiresScan;
        public DashboardDataSource(ClashDatabase database,Func<string> document,Func<string,bool> stale,Func<bool>? requiresScan=null){_database=database;_document=document;_stale=stale;_requiresScan=requiresScan??(()=>false);}
        public DashboardSnapshot Read(string scanId,string comparisonId)
        {
            bool frozen=scanId.StartsWith("snapshot:",StringComparison.Ordinal);if(frozen)scanId=scanId.Substring(9);
            var snapshot=new DashboardSnapshot {DocumentKey=_document()};
            if(snapshot.DocumentKey=="")return snapshot;
            var repository=_database.History;
            snapshot.Attempts=repository.GetVersions().OrderByDescending(v=>v.SequenceNumber).ToList();
            snapshot.Versions=snapshot.Attempts.Where(v=>v.State==ScanVersionState.Completed).OrderByDescending(v=>v.SequenceNumber).ToList();
            var latest=snapshot.Versions.FirstOrDefault();
            snapshot.Selected=scanId==""?latest:snapshot.Versions.FirstOrDefault(v=>v.ScanId==scanId)??latest;
            snapshot.IsCurrent=snapshot.Selected==latest&&!frozen;
            snapshot.RequiresFullScan=snapshot.IsCurrent&&_requiresScan();
            snapshot.Rows=(snapshot.IsCurrent?repository.GetCurrent():repository.GetObservations(snapshot.Selected!.ScanId)).ToList();
            if(snapshot.IsCurrent){var known=repository.GetKnown().ToDictionary(r=>r.ClashId);snapshot.Rows=snapshot.Rows.Select(r=>known[r.ClashId]).ToList();}
            if(snapshot.Selected!=null)
            {
                var selected=snapshot.Selected;
                snapshot.ScanRows=repository.GetObservations(selected.ScanId).ToList();
                var prior=snapshot.Versions.Where(v=>v.SequenceNumber<selected.SequenceNumber).ToList();
                var baseline=comparisonId!=""?prior.FirstOrDefault(v=>v.ScanId==comparisonId):
                    prior.FirstOrDefault(v=>ScanComparisonService.SameConfiguration(v.Capture,selected.Capture)&&v.Capture.ScopeJson==selected.Capture.ScopeJson)??prior.FirstOrDefault();
                snapshot.BaselineRows=baseline==null?new System.Collections.Generic.List<ClashObservation>():repository.GetObservations(baseline.ScanId).ToList();
                snapshot.BaselineGroups=baseline==null?new System.Collections.Generic.List<GroupScanRevision>():repository.GetGroups(baseline.ScanId).ToList();
                snapshot.Lineage=repository.GetLineage(selected.ScanId).ToList();
                snapshot.Comparison=new ScanComparisonService().Compare(baseline==null?null:new ScanSnapshot {Version=baseline,Issues=repository.GetObservations(baseline.ScanId).ToList()},
                    new ScanSnapshot {Version=selected,Issues=repository.GetObservations(selected.ScanId).ToList()});
                var changes=snapshot.Comparison.Issues.ToDictionary(c=>c.ClashId);
                foreach(var row in snapshot.Rows)if(changes.TryGetValue(row.ClashId,out var change)){row.ChangeKind=change.Kind;row.ChangeFlags=change.Flags;row.EvaluationReason=change.Reason;}
                // Current archive policy removes rows from the workspace, so drill-down counts use its visible identity set.
                var currentIds=snapshot.Rows.Select(r=>r.ClashId).ToHashSet();
                snapshot.VersionComparison=snapshot.Comparison;
                snapshot.Comparison=Newtonsoft.Json.JsonConvert.DeserializeObject<ScanComparison>(Newtonsoft.Json.JsonConvert.SerializeObject(snapshot.VersionComparison))!;
                snapshot.Comparison.Issues=snapshot.Comparison.Issues.Where(c=>currentIds.Contains(c.ClashId)).ToList();
                snapshot.Groups=repository.GetGroups(selected.ScanId).ToList();
                if(snapshot.IsCurrent)foreach(var group in snapshot.Groups)group.CoordinationJson=Newtonsoft.Json.JsonConvert.SerializeObject(repository.ReadGroupState(group.GroupKey));
            }
            snapshot.Metrics=DashboardMetricPolicy.Calculate(snapshot.Rows,snapshot.Comparison,snapshot.Selected?.EndedAtUtc,snapshot.IsCurrent?_stale:(Func<string,bool>?)null);
            return snapshot;
        }
        public AnalyticsHistory ReadAnalytics(string selectedScanId)
        {
            var versions=_database.History.GetVersions();var selected=versions.Single(v=>v.ScanId==selectedScanId);
            return new AnalyticsHistory {Scans=versions.Where(v=>v.State==ScanVersionState.Completed&&v.SequenceNumber<=selected.SequenceNumber).Select(v=>new ScanSnapshot {Version=v,Issues=_database.History.GetObservations(v.ScanId)}).ToList(),Events=_database.History.GetDocumentEvents().ToList()};
        }
        public System.Collections.Generic.IReadOnlyList<HistoryEntry> Timeline(string id)
        {
            var repository=_database.History;var versions=repository.GetVersions().ToDictionary(v=>v.ScanId);
            var observations=repository.GetIssueHistory(id).Select(record=>{var v=versions[record.ScanId];var r=record.Observation;return new HistoryEntry {WhenUtc=v.EndedAtUtc,ClashId=id,Lane="Scan observation",Scan=$"V{v.SequenceNumber:D3}",Time=v.EndedAtUtc?.ToLocalTime().ToString("g")??"",Workflow=r.Status,Change=r.ChangeKind?.ToString()??r.ObservationKind.ToString(),Flags=r.ChangeFlags.ToString(),Detail=r.ObservationKind+": "+r.EvaluationReason+" · "+r.GeometryEvidence};});
            var events=repository.GetEvents(id).Select(e=>new HistoryEntry {WhenUtc=e.TimestampKind=="Utc"&&DateTime.TryParse(e.Timestamp,out var eventUtc)?eventUtc.ToUniversalTime():(DateTime?)null,ClashId=id,Lane=e.Origin=="Scan"?"Scan lifecycle":"User workflow / coordination",Scan=e.ScanId!=null?$"V{versions[e.ScanId].SequenceNumber:D3}":"",Time=e.TimestampKind=="Utc"&&DateTime.TryParse(e.Timestamp,out var utc)?utc.ToLocalTime().ToString("g"):e.Timestamp+" ("+e.TimestampKind+")",Workflow=e.FromStatus+" → "+e.ToStatus,Detail=e.Author+": "+e.Comment});
            return observations.Concat(events).OrderBy(e=>e.WhenUtc??DateTime.MaxValue).ThenBy(e=>e.Lane).ToList();
        }        public string ReadHistory(string id)
        {
            var repository=_database.History;
            var versions=repository.GetVersions().ToDictionary(v=>v.ScanId);
            var observations=repository.GetIssueHistory(id).Select(record=>{
                var v=versions[record.ScanId];var r=record.Observation;
                return $"V{v.SequenceNumber:D3} · {v.EndedAtUtc?.ToLocalTime():g} · {r.ObservationKind} · {r.ChangeKind} · {r.Status} · seen {r.SeenInScanCount}\n{r.EvaluationReason}";
            });
            var events=repository.GetEvents(id).Select(e=>$"{e.Timestamp} ({e.TimestampKind}) · {e.Origin} · {e.Author}\n{e.FromStatus} → {e.ToStatus} · {e.Comment}");
            return "SCAN OBSERVATIONS\n"+string.Join("\n\n",observations)+"\n\nLIFECYCLE / COORDINATION EVENTS\n"+string.Join("\n\n",events);
        }
    }
}
