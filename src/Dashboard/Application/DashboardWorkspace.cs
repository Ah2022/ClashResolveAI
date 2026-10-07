using System;
using System.Collections.Generic;
using System.Linq;
using ClashResolveAI.Dashboard.Domain;
using Newtonsoft.Json.Linq;

namespace ClashResolveAI.Dashboard.Application
{
    public sealed class DashboardSnapshot
    {
        public string DocumentKey { get; set; }="";
        public List<ScanVersion> Attempts {get;set;}=new List<ScanVersion>();
        public List<ClashObservation> ScanRows {get;set;}=new List<ClashObservation>();
        public List<ClashObservation> BaselineRows {get;set;}=new List<ClashObservation>();
        public List<GroupScanRevision> BaselineGroups {get;set;}=new List<GroupScanRevision>();
        public List<GroupLineage> Lineage {get;set;}=new List<GroupLineage>();
        public List<ScanVersion> Versions { get; set; }=new List<ScanVersion>();
        public ScanVersion? Selected { get; set; }
        public bool IsCurrent { get; set; }
        public bool RequiresFullScan { get; set; }
        public List<ClashObservation> Rows { get; set; }=new List<ClashObservation>();
        public List<GroupScanRevision> Groups { get; set; }=new List<GroupScanRevision>();
        public ScanComparison? VersionComparison {get;set;}
        public ScanComparison? Comparison { get; set; }
        public DashboardMetrics Metrics { get; set; }=new DashboardMetrics();
    }
    public interface IDashboardDataSource
    {
        DashboardSnapshot Read(string scanId,string comparisonId);
        string ReadHistory(string clashId);
    }
    // State belongs to this workspace, never to a mutable detector result.
    public sealed class DashboardWorkspace
    {
        private readonly IDashboardDataSource _source;
        private readonly LifecycleCommandService _commands;
        private readonly GroupCoordinationService? _groups;
        public DashboardWorkspace(IDashboardDataSource source,LifecycleCommandService commands,GroupCoordinationService? groups=null){_source=source;_commands=commands;_groups=groups;}
        public DashboardSnapshot Snapshot { get; private set; }=new DashboardSnapshot();
        public string ScanId { get; set; }="";
        public string ComparisonId { get; set; }="";
        public string SelectedId { get; set; }="";
        public HashSet<string> SelectedIds { get; }=new HashSet<string>();
        public string Search { get; set; }="";
        public string GroupId { get; set; }="";
        public bool ChangedOnly {get;set;}
        public HashSet<string>? DrillIds {get;set;}
        public string QuickFilter { get; set; }="";
        public Core.ResultTypes ResultTypes { get; set; }=Core.ResultTypes.All;
        private string _mode="";
        public Dictionary<string,string> Filters { get; }=new Dictionary<string,string>();
        public void Refresh(bool latest=false)
        {
            if(latest){ScanId="";ComparisonId="";}
            var next=_source.Read(ScanId,ComparisonId);
            if(next.DocumentKey!=Snapshot.DocumentKey){Filters.Clear();DrillIds=null;ChangedOnly=false;Search="";GroupId="";QuickFilter="";SelectedId="";SelectedIds.Clear();ResultTypes=Core.ResultTypes.All;_mode="";ScanId="";ComparisonId="";next=_source.Read("","");}
            Snapshot=next;
            var mode=next.Selected?.Capture.ScanMode??"";
            if(mode!=""&&mode!=_mode){ResultTypes=mode=="HardOnly"?Core.ResultTypes.Hard|Core.ResultTypes.PossibleHard:Core.ResultTypes.All;_mode=mode;}
            if(!Snapshot.Rows.Any(c=>c.ClashId==SelectedId))SelectedId="";
            SelectedIds.IntersectWith(Snapshot.Rows.Select(c=>c.ClashId));
        }
        public static string Metadata(ClashObservation row,string key)=>JObject.Parse(row.MetadataJson)[key]?.ToString()??"";
        public static DateTime? DueLocal(ClashObservation row)=>JObject.Parse(row.MetadataJson)["DueDate"]?.ToObject<DateTime?>()?.ToLocalTime();
        public static string Field(ClashObservation row,string key)
        {
            if(key=="Owner")return Metadata(row,"AssignedEngineer");
            if(key=="Change")return row.ChangeKind?.ToString()??"";
            if(key=="Flags")return row.ChangeFlags.ToString();
            if(key=="Linked model")return row.LinkFileA+" | "+row.LinkFileB;
            return typeof(ClashObservation).GetProperty(key)?.GetValue(row)?.ToString()??"";
        }
        public List<ClashObservation> Visible()=>DashboardMetricPolicy.Rank(Snapshot.Rows.Where(row=>
            (DrillIds==null||DrillIds.Contains(row.ClashId))&&(ResultTypes&Type(row))!=0&&
            (!ChangedOnly||(row.ChangeKind!=ClashChangeKind.Persistent&&row.ChangeKind!=ClashChangeKind.UnchangedResolved)||row.ChangeFlags!=ClashChangeFlags.None)&&
            (GroupId==""||row.GroupId==GroupId)&&
            (QuickFilter!="Open"||LifecycleCommandService.Actionable(row.Status))&&
            (QuickFilter!="Critical"||(LifecycleCommandService.Actionable(row.Status)&&row.TestType!="Unverified"&&DashboardMetricPolicy.Critical(row)))&&
            (QuickFilter!="Unverified"||(row.ObservationKind==ScanObservationKind.Observed&&row.TestType=="Unverified"))&&
            (QuickFilter!="Health"||(row.Status!="Ignored"&&row.Status!="Approved"&&row.TestType!="Unverified"&&(row.ObservationKind==ScanObservationKind.Observed||row.ObservationKind==ScanObservationKind.VerifiedAbsent)))&&
            Filters.All(f=>f.Value==""||(f.Key=="Flags"?Field(row,f.Key).Split(',').Any(flag=>flag.Trim()==f.Value):Field(row,f.Key).Equals(f.Value,StringComparison.OrdinalIgnoreCase)))&&
            (Search==""||string.Join(" ",row.ClashId,row.ElementAId,row.ElementBId,row.FamilyTypeA,row.FamilyTypeB,row.CategoryNameA,row.CategoryNameB,row.LevelName,row.GridRef,Metadata(row,"AssignedEngineer")).IndexOf(Search,StringComparison.OrdinalIgnoreCase)>=0))).ToList();
        private static Core.ResultTypes Type(ClashObservation row)=>row.TestType=="HardClash"?Core.ResultTypes.Hard:row.TestType=="ClearanceClash"?Core.ResultTypes.Clearance:row.UnverifiedReason=="SolidTest"?Core.ResultTypes.PossibleHard:Core.ResultTypes.Unverified;
        public IReadOnlyList<Core.ClashStatus> Allowed(IEnumerable<string> ids)
        {
            var rows=Snapshot.Rows.Where(c=>ids.Contains(c.ClashId)).ToList();
            return !Snapshot.IsCurrent||rows.Count==0?Array.Empty<Core.ClashStatus>():LifecycleCommandService.Allowed(rows[0].Status).Where(s=>rows.All(r=>LifecycleCommandService.Allowed(r.Status).Contains(s))).ToArray();
        }
        public void ChangeStatus(IEnumerable<string> ids,Core.ClashStatus target,string reason)
        { EnsureCurrent();_commands.ChangeStatus(ids,target,Environment.UserName,reason);Refresh(); }
        public void SaveMetadata(IEnumerable<string> ids,string owner,DateTime? due,string comment)
        { EnsureCurrent();_commands.UpdateMetadata(ids,owner,due?.ToUniversalTime(),comment,Environment.UserName);Refresh(); }
        private void EnsureCurrent(){if(!Snapshot.IsCurrent)throw new InvalidOperationException("Historical scans are read-only. Select the latest scan to edit current issues.");}
        public List<GroupView> GroupViews()=>GroupCoordinationService.Project(Snapshot);
        public void AssignGroup(string key,string owner,DateTime? due){EnsureCurrent();(_groups??throw new InvalidOperationException("Group commands unavailable")).Assign(Snapshot.Selected!.ScanId,key,owner,due?.ToUniversalTime(),Environment.UserName);Refresh();}
        public void GroupStatus(string key,Core.ClashStatus target,string reason){EnsureCurrent();(_groups??throw new InvalidOperationException("Group commands unavailable")).Status(Snapshot.Selected!.ScanId,key,target,Environment.UserName,reason);Refresh();}
        public IReadOnlyList<HistoryEntry> Timeline(string id)=>(_source as IDashboardHistorySource)?.Timeline(id)??Array.Empty<HistoryEntry>();
        public AnalyticsHistory AnalyticsHistory()=>Snapshot.Selected==null?new AnalyticsHistory():(_source as IDashboardAnalyticsSource)?.ReadAnalytics(Snapshot.Selected.ScanId)??new AnalyticsHistory();
        public string History(string id)=>_source.ReadHistory(id);
    }
}
