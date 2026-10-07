using ClashResolveAI.Core;
using ClashResolveAI.Dashboard.Domain;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Dashboard.Application
{
    public interface IClashCommandStore
    {
        ClashObservation? ReadCurrentIssue(string id);
        void CommitMutations(IReadOnlyList<IssueMutation> mutations);
    }
    public sealed class LifecycleCommandService
    {
        private readonly IClashCommandStore _store;
        public LifecycleCommandService(IClashCommandStore store){_store=store;}
        private static readonly Dictionary<ClashStatus,ClashStatus[]> Transitions=new Dictionary<ClashStatus,ClashStatus[]> {
            [ClashStatus.New]=new[]{ClashStatus.Active,ClashStatus.InReview,ClashStatus.Approved,ClashStatus.OnSite,ClashStatus.Resolved,ClashStatus.Ignored,ClashStatus.Closed},
            [ClashStatus.Active]=new[]{ClashStatus.InReview,ClashStatus.Approved,ClashStatus.OnSite,ClashStatus.Resolved,ClashStatus.Ignored,ClashStatus.Closed},
            [ClashStatus.InReview]=new[]{ClashStatus.Active,ClashStatus.Approved,ClashStatus.OnSite,ClashStatus.Resolved,ClashStatus.Ignored,ClashStatus.Closed},
            [ClashStatus.Approved]=new[]{ClashStatus.InReview,ClashStatus.OnSite,ClashStatus.Resolved,ClashStatus.Ignored,ClashStatus.Closed},
            [ClashStatus.OnSite]=new[]{ClashStatus.InReview,ClashStatus.Approved,ClashStatus.Resolved,ClashStatus.Ignored,ClashStatus.Closed},
            [ClashStatus.Resolved]=new[]{ClashStatus.Reopened,ClashStatus.Closed},
            [ClashStatus.Ignored]=new[]{ClashStatus.Reopened,ClashStatus.Closed},
            [ClashStatus.Closed]=new[]{ClashStatus.Reopened},
            [ClashStatus.Reopened]=new[]{ClashStatus.Active,ClashStatus.InReview,ClashStatus.Approved,ClashStatus.OnSite,ClashStatus.Resolved,ClashStatus.Ignored,ClashStatus.Closed}
        };
        public static IReadOnlyList<ClashStatus> Allowed(string status)=>Enum.TryParse(status,out ClashStatus value)&&Transitions.TryGetValue(value,out var targets)?targets:Array.Empty<ClashStatus>();
        public static bool RequiresReason(ClashStatus state)=>new[]{ClashStatus.Approved,ClashStatus.Resolved,ClashStatus.Ignored,ClashStatus.Closed,ClashStatus.Reopened}.Contains(state);
        public static bool Actionable(string status)=>new[]{"New","Active","InReview","OnSite","Reopened"}.Contains(status);
        public void ChangeStatus(IEnumerable<string> ids,ClashStatus target,string author,string reason)
        {
            var mutations=new List<IssueMutation>();
            foreach(var id in ids.Distinct()) {
                var row=_store.ReadCurrentIssue(id)??throw new InvalidOperationException("Issue "+id+" is no longer current.");
                if(row.Status==target.ToString())continue;
                if(!Allowed(row.Status).Contains(target))throw new InvalidOperationException(row.Status+" → "+target+" is not an allowed transition for "+id+".");
                if(RequiresReason(target)&&string.IsNullOrWhiteSpace(reason))throw new InvalidOperationException("Enter a reason for "+target+".");
                var mutation=new IssueMutation {Snapshot=row,ExpectedStatus=row.Status,ExpectedMetadataJson=row.MetadataJson,Author=author,Reason=reason};
                row.Status=target.ToString();
                if(target==ClashStatus.Resolved)row.ResolvedAtUtc=DateTime.UtcNow;
                if(target==ClashStatus.Reopened){row.ReopenedAtUtc=DateTime.UtcNow;row.OpenEpisodeAtUtc=row.ReopenedAtUtc;}
                mutations.Add(mutation);
            }
            _store.CommitMutations(mutations);
        }
        public void UpdateMetadata(IEnumerable<string> ids,string owner,DateTime? dueDateUtc,string comment,string author)
        {
            if(dueDateUtc.HasValue&&dueDateUtc.Value.Kind!=DateTimeKind.Utc)throw new ArgumentException("Due date must be UTC.");
            var mutations=new List<IssueMutation>();
            foreach(var id in ids.Distinct()) {
                var row=_store.ReadCurrentIssue(id)??throw new InvalidOperationException("Issue is no longer current.");
                var original=row.MetadataJson;var metadata=JObject.Parse(original);
                metadata["OwnerOverride"]=true;metadata["DueOverride"]=true;
                metadata["AssignedEngineer"]=owner;metadata["DueDate"]=dueDateUtc.HasValue?JToken.FromObject(dueDateUtc.Value):JValue.CreateNull();
                if(!string.IsNullOrWhiteSpace(comment))metadata["Comments"]=((string?)metadata["Comments"]??"")+(string.IsNullOrEmpty((string?)metadata["Comments"])?"":"\n")+comment.Trim();
                row.MetadataJson=metadata.ToString(Newtonsoft.Json.Formatting.None);
                if(JToken.DeepEquals(JObject.Parse(original),metadata))continue;
                mutations.Add(new IssueMutation {Snapshot=row,ExpectedStatus=row.Status,ExpectedMetadataJson=original,Origin="UserMetadata",Author=author,
                    Reason=Newtonsoft.Json.JsonConvert.SerializeObject(new {Owner=owner,DueDateUtc=dueDateUtc,Comment=comment})});
            }
            _store.CommitMutations(mutations);
        }
    }
}
