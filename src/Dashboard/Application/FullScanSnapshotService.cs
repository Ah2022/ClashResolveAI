using ClashResolveAI.Core;
using ClashResolveAI.ClashEngine;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Dashboard.Persistence;
using ClashResolveAI.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ClashResolveAI.Dashboard.Application
{
    public sealed class FullScanSnapshotBatch
    {
        public List<ClashResult> Rows { get; set; } = new List<ClashResult>();
        public List<ClashObservation> Observations { get; set; } = new List<ClashObservation>();
        public List<GroupScanRevision> Groups { get; set; } = new List<GroupScanRevision>();
        public ScanComparison Comparison { get; set; } = new ScanComparison();
        public int RetainedClearance { get; set; }
    }
    public static class FullScanSnapshotService
    {
        // Compatibility entry point for isolated fixture scans. Coordinator completion always supplies persisted versions.
        public static FullScanSnapshotBatch Stage(IEnumerable<ClashResult> current,IEnumerable<ClashResult> found,ScanStatistics stats,long revision)
        {
            var old=current.Select(ClashObservationAdapter.Capture).ToList();
            ScanVersion Version(int sequence)=>new ScanVersion {SequenceNumber=sequence,ScanId=sequence==1?"fixture-previous":"fixture-current",Capture=new ScanCapture {DocumentKey=old.FirstOrDefault()?.DocumentKey??"",ConfigurationFingerprint="fixture",EngineVersion="fixture"}};
            return Stage(current,found,stats,revision,old.Count==0?null:new ScanSnapshot {Version=Version(1),Issues=old},Version(2));
        }
        public static FullScanSnapshotBatch Stage(IEnumerable<ClashResult> current,IEnumerable<ClashResult> found,ScanStatistics stats,long revision,ScanSnapshot? baseline,ScanVersion version)
        {
            var old=current.ToDictionary(c=>c.NormalizedKey,ClashObservationAdapter.Capture);
            var map=new Dictionary<string,ClashObservation>(old.ToDictionary(p=>p.Key,p=>JsonConvert.DeserializeObject<ClashObservation>(JsonConvert.SerializeObject(p.Value))!));
            var seen=new HashSet<string>();var now=DateTime.UtcNow;
            foreach(var result in found.Where(c=>c.Origin==ResultOrigin.Full)) {
                var row=ClashObservationAdapter.Capture(result);
                if(!seen.Add(row.NormalizedKey))throw new InvalidOperationException("Duplicate scan pair.");
                if(old.TryGetValue(row.NormalizedKey,out var prior)) {
                    if(!ScanComparisonService.SameEndpoints(prior,row))throw new InvalidOperationException("Element identity was replaced; issue identity migration is required.");
                    row.MetadataJson=prior.MetadataJson;row.DetectedAt=prior.DetectedAt;row.TimestampKind=prior.TimestampKind;row.DetectedAtOriginalText=prior.DetectedAtOriginalText;
                    row.AiSuggestion=prior.AiSuggestion;row.RfiText=prior.RfiText;row.Status=prior.Status;
                    row.ResolvedAtUtc=prior.ResolvedAtUtc;row.ReopenedAtUtc=prior.ReopenedAtUtc;row.OpenEpisodeAtUtc=prior.OpenEpisodeAtUtc;
                    row.SeenInScanCount=prior.SeenInScanCount;row.ConsecutiveScanCount=prior.ConsecutiveScanCount;
                    if(prior.Status=="Resolved"&&row.TestType!="Unverified") {row.Status="Reopened";row.ReopenedAtUtc=now;row.OpenEpisodeAtUtc=now;}
                }
                row.ObservationKind=ScanObservationKind.Observed;row.EvaluationReason=row.TestType=="Unverified"?"Observed inconclusive evidence: "+row.UnverifiedReason:"Scan observed current physical evidence";
                row.LastSeenAtUtc=now;row.SeenInScanCount++;row.ConsecutiveScanCount++;row.OpenEpisodeAtUtc=row.OpenEpisodeAtUtc??(row.DetectedAt.Kind==DateTimeKind.Utc?row.DetectedAt:now);
                map[row.NormalizedKey]=row;
            }
            bool sameConfig=baseline!=null&&ScanComparisonService.SameConfiguration(baseline.Version.Capture,version.Capture);
            var earlier=(baseline?.Issues??Array.Empty<ClashObservation>()).ToDictionary(c=>c.NormalizedKey);
            int retained=0;
            foreach(var row in map.Values.Where(c=>!seen.Contains(c.NormalizedKey))) {
                var result=ClashObservationAdapter.Restore(row);
                bool mode=ResultLifecycle.Evaluated(stats.Mode,result),covered=stats.Scope.Contains(result);
                bool reliable=mode&&covered&&sameConfig&&earlier.ContainsKey(row.NormalizedKey)&&ScanComparisonService.LinksComparable(row,baseline!.Version.Capture,version.Capture);
                if(reliable) {
                    row.ObservationKind=ScanObservationKind.VerifiedAbsent;row.EvaluationReason="No finding after reliable comparable evaluation";row.ConsecutiveScanCount=0;
                    if(LifecycleCommandService.Actionable(row.Status)){row.Status="Resolved";row.ResolvedAtUtc=now;}
                } else {
                    row.ObservationKind=ScanObservationKind.NotEvaluated;
                    row.EvaluationReason=!mode?"Result type not evaluated by scan mode":!covered?"Outside reliable scan coverage":!sameConfig?"No comparable configuration baseline":"Linked model identity/load state/transform or earlier coverage differs";
                    if(row.TestType=="ClearanceClash")retained++;
                }
            }
            var issues=map.Values.OrderBy(c=>c.ClashId,StringComparer.Ordinal).ToList();
            var comparison=new ScanComparisonService().Compare(baseline,new ScanSnapshot {Version=version,Issues=issues});
            var changes=comparison.Issues.ToDictionary(c=>c.ClashId);
            foreach(var row in issues) {var change=changes[row.ClashId];row.ChangeKind=change.Kind;row.ChangeFlags=change.Flags;row.PreviousScanId=comparison.PreviousScanId;row.CurrentScanId=version.ScanId;}
            var rows=issues.Select(ClashObservationAdapter.Restore).ToList();
            var oldRevisions=current.ToDictionary(c=>c.ClashId,c=>c.GeometryRevision);
            foreach(var row in rows)row.GeometryRevision=seen.Contains(row.NormalizedKey)||row.ObservationKind=="VerifiedAbsent"?revision:oldRevisions.TryGetValue(row.ClashId,out long stamp)?stamp:-1;
            var groups=ClashObservationAdapter.CaptureGroups(new ClashGroupingEngine().GroupClashes(rows));
            var keys=groups.SelectMany(g=>g.MemberClashIds.Select(id=>new {Id=id,g.GroupKey})).ToDictionary(g=>g.Id,g=>g.GroupKey);
            foreach(var row in issues)row.GroupId=keys[row.ClashId];
            foreach(var row in rows)row.GroupId=keys[row.ClashId];
            return new FullScanSnapshotBatch {Rows=rows,Observations=issues,Groups=groups,Comparison=comparison,RetainedClearance=retained};
        }
    }
}
