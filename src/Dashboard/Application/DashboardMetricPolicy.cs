using ClashResolveAI.Dashboard.Domain;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Dashboard.Application
{
    public static class DashboardMetricPolicy
    {
        public static bool Critical(ClashObservation row)=>row.Severity=="Critical"||row.Priority=="Critical";
        public static DashboardMetrics Calculate(IReadOnlyList<ClashObservation> rows,ScanComparison? comparison,DateTime? completedUtc,Func<string,bool>? stale=null)
        {
            var eligible=rows.Where(c=>c.Status!="Ignored"&&c.Status!="Approved"&&c.TestType!="Unverified"&&(c.ObservationKind==ScanObservationKind.Observed||c.ObservationKind==ScanObservationKind.VerifiedAbsent)).ToList();
            double weight=eligible.Sum(c=>Critical(c)?3.0:1.0),resolved=eligible.Where(c=>c.ObservationKind==ScanObservationKind.VerifiedAbsent).Sum(c=>Critical(c)?3.0:1.0);
            return new DashboardMetrics {
                Open=rows.Count(c=>LifecycleCommandService.Actionable(c.Status)),Critical=rows.Count(c=>LifecycleCommandService.Actionable(c.Status)&&c.TestType!="Unverified"&&Critical(c)),
                New=comparison?.Count(ClashChangeKind.New)??0,Reopened=comparison?.Count(ClashChangeKind.Reopened)??0,Resolved=comparison?.Count(ClashChangeKind.Resolved)??0,
                Unverified=rows.Count(c=>c.ObservationKind==ScanObservationKind.Observed&&c.TestType=="Unverified"),Stale=rows.Count(c=>stale?.Invoke(c.ClashId)==true),
                ActionableGroups=rows.Where(c=>LifecycleCommandService.Actionable(c.Status)).Select(c=>c.GroupId==""?c.ClashId:c.GroupId).Distinct().Count(),
                Health=weight==0?(double?)null:100*resolved/weight,HealthBasis=eligible.Count,LastScanUtc=completedUtc
            };
        }
        public static IEnumerable<ClashObservation> Rank(IEnumerable<ClashObservation> rows)
        {
            var materialized=rows.ToList();var impact=materialized.Where(c=>LifecycleCommandService.Actionable(c.Status)).GroupBy(c=>c.GroupId).ToDictionary(g=>g.Key,g=>g.Count());
            int Severity(string value)=>value=="Critical"?0:value=="Hard"?1:value=="Soft"?2:value=="Clearance"?3:99;
            return materialized.OrderBy(c=>LifecycleCommandService.Actionable(c.Status)?0:1).ThenBy(c=>Critical(c)&&c.TestType!="Unverified"?0:1)
                .ThenBy(c=>Severity(c.Severity)).ThenBy(c=>c.ChangeKind==ClashChangeKind.New||c.ChangeKind==ClashChangeKind.Reopened?0:1)
                .ThenBy(c=>c.TestType=="HardClash"?0:c.TestType=="ClearanceClash"?1:2)
                .ThenByDescending(c=>impact.TryGetValue(c.GroupId,out int count)?count:0)
                .ThenBy(c=>c.OpenEpisodeAtUtc??c.DetectedAt).ThenBy(c=>c.ClashId,StringComparer.Ordinal);
        }
    }
}
