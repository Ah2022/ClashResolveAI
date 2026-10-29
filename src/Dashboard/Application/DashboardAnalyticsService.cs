using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using ClashResolveAI.Dashboard.Domain;
namespace ClashResolveAI.Dashboard.Application
{
    public sealed class AnalyticsHistory
    {
        public List<ScanSnapshot> Scans {get;set;}=new List<ScanSnapshot>();
        public List<ClashLifecycleEvent> Events {get;set;}=new List<ClashLifecycleEvent>();
    }
    public interface IDashboardAnalyticsSource { AnalyticsHistory ReadAnalytics(string selectedScanId); }
    public sealed class AnalyticsBucket
    {
        public string Label {get;set;}="";
        public List<string> Ids {get;set;}=new List<string>();
        public int Count=>Ids.Count;
    }
    public sealed class ScanTrend
    {
        public string Version {get;set;}="";public string ScanId {get;set;}="";
        public string Baseline {get;set;}="";public string Coverage {get;set;}="";
        public int Open {get;set;}public int New {get;set;}public int Resolved {get;set;}public int Reopened {get;set;}
    }
    public sealed class DashboardAnalytics
    {
        public Dictionary<string,List<AnalyticsBucket>> Charts {get;set;}=new Dictionary<string,List<AnalyticsBucket>>();
        public List<ScanTrend> Trend {get;set;}=new List<ScanTrend>();
        public DashboardMetrics Metrics {get;set;}=new DashboardMetrics();
        public string Resolution {get;set;}="";
        public string Context {get;set;}="";
    }
    public static class DashboardAnalyticsService
    {
        public static DashboardAnalytics Calculate(DashboardSnapshot snapshot,IReadOnlyList<ClashObservation> rows,AnalyticsHistory history,DateTime utcNow)
        {
            var result=new DashboardAnalytics {Metrics=DashboardMetricPolicy.Calculate(rows,null,snapshot.Selected?.EndedAtUtc)};
            var ids=new HashSet<string>(rows.Select(r=>r.ClashId));
            var at=snapshot.IsCurrent?utcNow:snapshot.Selected?.EndedAtUtc??utcNow;
            result.Context=$"{(snapshot.IsCurrent?"Current workflow":"Captured historical state")} · V{snapshot.Selected?.SequenceNumber:D3} · {rows.Count} filtered issues · age at {at.ToLocalTime():g}";
            List<AnalyticsBucket> Buckets(IEnumerable<ClashObservation> source,Func<ClashObservation,string> key)=>source.GroupBy(key).Select(g=>new AnalyticsBucket {Label=g.Key,Ids=g.Select(r=>r.ClashId).Distinct().ToList()}).OrderByDescending(b=>b.Count).ThenBy(b=>b.Label).ToList();
            var open=rows.Where(r=>LifecycleCommandService.Actionable(r.Status)).ToList();
            result.Charts["Open by discipline pair"]=Buckets(open,r=>r.DisciplineA+" / "+r.DisciplineB);
            result.Charts["Confirmed critical by level"]=Buckets(open.Where(r=>r.TestType!="Unverified"&&DashboardMetricPolicy.Critical(r)),r=>r.LevelName==""?"Unspecified level":r.LevelName);
            result.Charts["Uncertainty reasons"]=Buckets(rows.Where(r=>r.ObservationKind==ScanObservationKind.Observed&&r.TestType=="Unverified"),r=>r.UnverifiedReason==""?"Unspecified reason":r.UnverifiedReason);
            result.Charts["Open age by status"]=Buckets(open,r=>r.Status+" · "+Age(r,at));
            result.Charts["Observed scan persistence"]=Buckets(rows,r=>r.SeenInScanCount<=1?"Observed once or legacy":r.SeenInScanCount<5?"Observed in 2–4 scans":"Observed in 5+ scans");
            var endpoints=rows.Where(r=>r.ObservationKind==ScanObservationKind.Observed).SelectMany(r=>new[]{new {Row=r,Key=r.DocumentKey+" | "+r.LinkInstanceA+" | "+r.ElementAId+" | "+r.ElementUniqueIdA},new {Row=r,Key=r.DocumentKey+" | "+r.LinkInstanceB+" | "+r.ElementBId+" | "+r.ElementUniqueIdB}});
            result.Charts["Observed component involvement (overlapping)"]=endpoints.GroupBy(e=>e.Key).Select(g=>new AnalyticsBucket {Label=g.Key,Ids=g.Select(e=>e.Row.ClashId).Distinct().ToList()}).OrderByDescending(b=>b.Count).ThenBy(b=>b.Label).ToList();
            ScanSnapshot? previous=null;
            foreach(var scan in history.Scans.OrderBy(s=>s.Version.SequenceNumber)){
                var comparison=new ScanComparisonService().Compare(previous,scan);var visible=scan.Issues.Where(r=>ids.Contains(r.ClashId)).ToList();var metrics=DashboardMetricPolicy.Calculate(visible,null,scan.Version.EndedAtUtc);
                result.Trend.Add(new ScanTrend {Version=$"V{scan.Version.SequenceNumber:D3}",ScanId=scan.Version.ScanId,Baseline=previous==null?"Initial baseline":$"V{previous.Version.SequenceNumber:D3}",Coverage=comparison.CoverageNotice,Open=metrics.Open,New=comparison.Issues.Count(i=>ids.Contains(i.ClashId)&&i.Kind==ClashChangeKind.New),Resolved=comparison.Issues.Count(i=>ids.Contains(i.ClashId)&&i.Kind==ClashChangeKind.Resolved),Reopened=comparison.Issues.Count(i=>ids.Contains(i.ClashId)&&i.Kind==ClashChangeKind.Reopened)});previous=scan;
            }
            var baseline=history.Scans.FirstOrDefault(s=>s.Version.ScanId==snapshot.Comparison?.PreviousScanId);
            if(baseline==null){result.Resolution="Resolution rate unavailable: select a completed comparison baseline.";return result;}
            var cohort=baseline.Issues.Where(r=>ids.Contains(r.ClashId)&&LifecycleCommandService.Actionable(r.Status)).Select(r=>r.ClashId).ToHashSet();
            var start=baseline.Version.EndedAtUtc!.Value;
            var dated=history.Events.Where(e=>e.TimestampKind=="Utc"&&DateTime.TryParse(e.Timestamp,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out _)).Select(e=>new {Event=e,Time=DateTime.Parse(e.Timestamp,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime()}).Where(e=>e.Time<=at).OrderBy(e=>e.Time).ToList();
            var resolutions=dated.Where(e=>e.Time>start&&e.Event.ToStatus=="Resolved"&&e.Event.FromStatus!="Resolved"&&cohort.Contains(e.Event.ClashId)).ToList();
            var elapsed=new List<double>();
            foreach(var end in resolutions){var beginning=dated.LastOrDefault(e=>e.Time<end.Time&&e.Event.ClashId==end.Event.ClashId&&LifecycleCommandService.Actionable(e.Event.ToStatus)&&!LifecycleCommandService.Actionable(e.Event.FromStatus));if(beginning!=null)elapsed.Add((end.Time-beginning.Time).TotalDays);}
            int count=resolutions.Select(e=>e.Event.ClashId).Distinct().Count();
            result.Resolution=$"Baseline actionable cohort: {cohort.Count} filtered issues; {count} entered Resolved during {start.ToLocalTime():g} → {at.ToLocalTime():g}. "+(cohort.Count==0?"Rate unavailable (empty denominator).":$"Rate: {100.0*count/cohort.Count:0.#}%.")+" This measures workflow throughput; an issue may subsequently reopen. "+(elapsed.Count==0?"Resolution duration unavailable: no complete dated lifecycle episodes.":$"Mean dated episode duration: {elapsed.Average():0.##} days ({elapsed.Count} events).")+" Unknown-timezone events are excluded. "+snapshot.Comparison?.CoverageNotice;
            return result;
        }
        private static string Age(ClashObservation row,DateTime at)
        {
            var begin=row.OpenEpisodeAtUtc??(row.TimestampKind=="Utc"?(DateTime?)row.DetectedAt.ToUniversalTime():null);
            if(!begin.HasValue)return "Age unknown";var days=Math.Max(0,(at-begin.Value).TotalDays);return days<7?"0–6 days":days<30?"7–29 days":"30+ days";
        }
    }
}
