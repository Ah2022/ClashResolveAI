using System;
using System.Collections.Generic;
using System.Linq;
using ClashResolveAI.Core;
using ClashResolveAI.Dashboard.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace ClashResolveAI.Dashboard.Application
{
    public sealed class GroupCoordinationState
    {
        public string GroupKey {get;set;}="";
        public string Owner {get;set;}="";
        public DateTime? DueUtc {get;set;}
        public string Note {get;set;}="";
    }
    public sealed class GroupLineage
    {
        public string Parent {get;set;}="";public string Child {get;set;}="";public string Kind {get;set;}="";
    }
    public sealed class GroupOffenderReference
    {
        public string DocumentKey {get;set;}="";
        public string LinkInstanceUniqueId {get;set;}="";
        public long ElementId {get;set;}
        public string ElementUniqueId {get;set;}="";
        public string Identity=>DocumentKey+"|"+LinkInstanceUniqueId+"|"+ElementId+"|"+ElementUniqueId;
    }
    public sealed class GroupView
    {
        public GroupScanRevision Revision {get;set;}=new GroupScanRevision();
        public string Key=>Revision.GroupKey;public string Title=>Revision.Title;public string Reason=>Revision.Reason;
        public GroupOffenderReference? PrimaryOffender {get;set;}
        public string Offender=>PrimaryOffender==null?"No primary offender established":"Shared component: "+PrimaryOffender.ElementId;
        public string OffenderIdentity=>PrimaryOffender?.Identity??"";
        public List<ClashObservation> Members {get;set;}=new List<ClashObservation>();
        public List<string> CurrentIds {get;set;}=new List<string>();
        public string State=>string.Join(", ",Members.GroupBy(r=>r.Status).OrderBy(g=>g.Key).Select(g=>$"{g.Key}: {g.Count()}"));
        public string Evidence=>string.Join(", ",Members.GroupBy(r=>r.TestType).Select(g=>$"{g.Key}: {g.Count()}"));
        public int Open {get {var ids=new HashSet<string>(CurrentIds);return Members.Count(r=>ids.Contains(r.ClashId)&&LifecycleCommandService.Actionable(r.Status));}}
        public int New=>Members.Count(r=>r.ChangeKind==ClashChangeKind.New);public int Resolved=>Members.Count(r=>r.ChangeKind==ClashChangeKind.Resolved);
        public int Count=>Members.Count;
        public DateTime? Oldest {get {var ids=new HashSet<string>(CurrentIds);return Members.Where(r=>ids.Contains(r.ClashId)&&LifecycleCommandService.Actionable(r.Status)).Select(r=>r.OpenEpisodeAtUtc??r.DetectedAt).Cast<DateTime?>().OrderBy(d=>d).FirstOrDefault();}}
        public string Owner {get;set;}="";public DateTime? Due {get;set;}
        public string EffectiveOwners=>string.Join(", ",Members.Select(r=>DashboardWorkspace.Metadata(r,"AssignedEngineer")).Distinct());
        public string Lineage {get;set;}="";
    }
    public interface IGroupCommandStore:IClashCommandStore
    {
        GroupCoordinationState ReadGroupState(string key);
        IReadOnlyList<string> GroupMembers(string scanId,string key);
        void ChangeGroupStatus(string scanId,string key,ClashStatus target,string author,string reason);
        void CommitGroup(string scanId,GroupCoordinationState expected,GroupCoordinationState state,IReadOnlyList<IssueMutation> changes,string author);
    }
    public sealed class GroupCoordinationService
    {
        private readonly IGroupCommandStore _store;
        public GroupCoordinationService(IGroupCommandStore store){_store=store;}
        public void Assign(string scanId,string key,string owner,DateTime? dueUtc,string author)
        {
            if(dueUtc.HasValue&&dueUtc.Value.Kind!=DateTimeKind.Utc)throw new ArgumentException("Group due date must be UTC.");
            var expected=_store.ReadGroupState(key);var changes=new List<IssueMutation>();
            foreach(string id in _store.GroupMembers(scanId,key)){
                var row=_store.ReadCurrentIssue(id);if(row==null)continue;
                var metadata=JObject.Parse(row.MetadataJson);var original=row.MetadataJson;
                ApplyDefault(metadata,key,owner,dueUtc);
                row.MetadataJson=metadata.ToString(Formatting.None);
                if(!JToken.DeepEquals(JObject.Parse(original),metadata))changes.Add(new IssueMutation {Snapshot=row,ExpectedStatus=row.Status,ExpectedMetadataJson=original,Origin="GroupAssignment",Author=author,Reason="Group defaults: "+key});
            }
            _store.CommitGroup(scanId,expected,new GroupCoordinationState {GroupKey=key,Owner=owner,DueUtc=dueUtc},changes,author);
        }
        public static void ApplyDefault(JObject metadata,string key,string owner,DateTime? due)
        {
            bool inherited=(string?)metadata["InheritedGroupKey"]!=""&&metadata["InheritedGroupKey"]!=null;
            if(!inherited&&!string.IsNullOrEmpty((string?)metadata["AssignedEngineer"]))metadata["OwnerOverride"]=true;
            if(!inherited&&metadata["DueDate"]!=null&&metadata["DueDate"]!.Type!=JTokenType.Null)metadata["DueOverride"]=true;
            if((bool?)metadata["OwnerOverride"]!=true&&(inherited||string.IsNullOrEmpty((string?)metadata["AssignedEngineer"])))metadata["AssignedEngineer"]=owner;
            if((bool?)metadata["DueOverride"]!=true&&(inherited||metadata["DueDate"]==null||metadata["DueDate"]!.Type==JTokenType.Null))metadata["DueDate"]=due.HasValue?JToken.FromObject(due.Value):JValue.CreateNull();
            metadata["InheritedGroupKey"]=key;
        }
        public void Status(string scanId,string key,ClashStatus target,string author,string reason)
        {_store.ChangeGroupStatus(scanId,key,target,author,reason);}
        public static List<GroupLineage> Relate(IReadOnlyList<GroupScanRevision> previous,IReadOnlyList<GroupScanRevision> current)
        {
            var index=previous.SelectMany(g=>g.MemberClashIds.Select(id=>new {Key=g.Reason+"|"+id,Group=g})).GroupBy(p=>p.Key).ToDictionary(g=>g.Key,g=>g.Select(p=>p.Group).ToList());
            var pairs=new Dictionary<string,GroupLineage>();
            foreach(var child in current)foreach(var member in child.MemberClashIds)if(index.TryGetValue(child.Reason+"|"+member,out var parents))foreach(var parent in parents)if(parent.GroupKey!=child.GroupKey)pairs[parent.GroupKey+"|"+child.GroupKey]=new GroupLineage {Parent=parent.GroupKey,Child=child.GroupKey};
            var links=pairs.Values.ToList();var splits=links.GroupBy(l=>l.Parent).ToDictionary(g=>g.Key,g=>g.Count());var merges=links.GroupBy(l=>l.Child).ToDictionary(g=>g.Key,g=>g.Count());
            foreach(var link in links){bool split=splits[link.Parent]>1,merge=merges[link.Child]>1;link.Kind=split&&merge?"SplitMerge":split?"Split":merge?"Merge":"MembershipChanged";}
            return links.OrderBy(l=>l.Parent).ThenBy(l=>l.Child).ToList();
        }
        public static GroupCoordinationState Inherit(string key,IEnumerable<GroupCoordinationState> parents)
        {
            var list=parents.ToList();var owners=list.Select(p=>p.Owner).Distinct().ToList();var dues=list.Select(p=>p.DueUtc).Distinct().ToList();
            return new GroupCoordinationState {GroupKey=key,Owner=owners.Count==1?owners[0]:"",DueUtc=dues.Count==1?dues[0]:null,Note=owners.Count>1||dues.Count>1?"Merge has conflicting defaults; issue assignments are retained. Review group assignment.":""};
        }
        public static List<GroupView> Project(DashboardSnapshot snapshot)
        {
            var currentIds=snapshot.Rows.GroupBy(r=>r.GroupId).ToDictionary(g=>g.Key,g=>g.Select(r=>r.ClashId).ToList());
            var all=snapshot.Groups.Concat(snapshot.BaselineGroups).GroupBy(g=>g.GroupKey).Select(g=>g.First());
            var rows=snapshot.Rows.Concat(snapshot.ScanRows).Concat(snapshot.BaselineRows).GroupBy(r=>r.ClashId).ToDictionary(g=>g.Key,g=>g.First());
            var changes=(snapshot.VersionComparison??snapshot.Comparison)?.Issues.ToDictionary(i=>i.ClashId);
            return all.Select(group=>{
                var members=group.MemberClashIds.Where(rows.ContainsKey).Select(id=>JsonConvert.DeserializeObject<ClashObservation>(JsonConvert.SerializeObject(rows[id]))!).ToList();
                foreach(var member in members)if(changes!=null&&changes.TryGetValue(member.ClashId,out var change)){member.ChangeKind=change.Kind;member.ChangeFlags=change.Flags;}
                var state=JsonConvert.DeserializeObject<GroupCoordinationState>(group.CoordinationJson)??new GroupCoordinationState();
                var offender=members.SelectMany(r=>new[]{new GroupOffenderReference {ElementId=r.ElementAId,ElementUniqueId=r.ElementUniqueIdA,LinkInstanceUniqueId=r.LinkInstanceA,DocumentKey=r.DocumentKey},new GroupOffenderReference {ElementId=r.ElementBId,ElementUniqueId=r.ElementUniqueIdB,LinkInstanceUniqueId=r.LinkInstanceB,DocumentKey=r.DocumentKey}}).Where(e=>e.ElementId>0).GroupBy(e=>e.Identity).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key).FirstOrDefault();
                return new GroupView {Revision=group,Members=members,CurrentIds=currentIds.TryGetValue(group.GroupKey,out var ids)?ids:new List<string>(),Owner=state.Owner,Due=state.DueUtc?.ToLocalTime(),Lineage=string.Join("; ",snapshot.Lineage.Where(l=>l.Child==group.GroupKey||l.Parent==group.GroupKey).Select(l=>l.Kind+": "+l.Parent.Substring(0,Math.Min(10,l.Parent.Length))+" → "+l.Child.Substring(0,Math.Min(10,l.Child.Length)))),PrimaryOffender=group.PrimaryOffender!=""&&offender?.Count()>1?offender.First():null,};
            }).OrderByDescending(g=>g.Open).ThenBy(g=>g.Key).ToList();
        }
    }
}
