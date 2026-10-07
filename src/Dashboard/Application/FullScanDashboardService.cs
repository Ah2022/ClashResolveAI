using ClashResolveAI.Core;
using ClashResolveAI.ClashEngine;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Dashboard.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Dashboard.Application
{
    public sealed class FullScanDashboardService
    {
        private readonly ClashDatabase _database;
        public FullScanDashboardService(ClashDatabase database){_database=database;}
        public FullScanSnapshotBatch Complete(IReadOnlyList<ClashResult> current,IReadOnlyList<ClashResult> found,ScanStatistics stats,long revision)
        {
            var versions=_database.History.GetVersions();
            var running=versions.Single(v=>v.ScanId==stats.ScanId&&v.State==ScanVersionState.Running);
            var completed=versions.Where(v=>v.State==ScanVersionState.Completed&&v.SequenceNumber<running.SequenceNumber).OrderByDescending(v=>v.SequenceNumber).ToList();
            var baseline=completed.FirstOrDefault(v=>ScanComparisonService.SameConfiguration(v.Capture,running.Capture)&&v.Capture.ScopeJson==running.Capture.ScopeJson)??completed.FirstOrDefault();
            var known=_database.History.GetKnown().ToDictionary(c=>c.NormalizedKey);
            var stagedCurrent=current.Select(c=> {
                var copy=known.TryGetValue(c.NormalizedKey,out var persisted)?persisted:ClashObservationAdapter.Capture(c);
                var result=ClashObservationAdapter.Restore(copy);result.GeometryRevision=c.GeometryRevision;return result;
            }).ToList();
            var active=new HashSet<string>(stagedCurrent.Select(c=>c.NormalizedKey));
            foreach(var row in found.Where(c=>c.Origin==ResultOrigin.Full))if(!active.Contains(row.NormalizedKey)&&known.TryGetValue(row.NormalizedKey,out var archived)){stagedCurrent.Add(ClashObservationAdapter.Restore(archived));active.Add(row.NormalizedKey);}
            var snapshot=baseline==null?null:new ScanSnapshot {Version=baseline,Issues=_database.History.GetObservations(baseline.ScanId)};
            var batch=FullScanSnapshotService.Stage(stagedCurrent,found,stats,revision,snapshot,running);
            _database.CommitFullScan(stats,batch.Rows,batch.Observations,batch.Groups);
            return batch;
        }
    }
}
