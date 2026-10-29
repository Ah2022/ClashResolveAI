using System;
using System.Collections.Generic;
using System.Linq;
using ClashResolveAI.Core;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Dashboard.Persistence;

namespace ClashResolveAI.Dashboard.Application
{
    public static class DashboardExportProjection
    {
        public static ClashGroup GroupTopic(GroupView group)
        {
            var ids=new HashSet<string>(group.CurrentIds);var rows=group.Members.Where(r=>ids.Contains(r.ClashId)).Select(ClashObservationAdapter.Restore).ToList();
            if(rows.Count==0)throw new InvalidOperationException("This group has no current members to export.");
            var states=rows.Select(r=>r.Status).Distinct().ToList();var owners=rows.Select(r=>r.Metadata.AssignedEngineer).Distinct().ToList();
            var metadata=new ClashMetadata {AssignedEngineer=group.Owner!=""?group.Owner:owners.Count==1?owners[0]:"",DueDate=group.Due?.ToUniversalTime(),ResolutionNotes="Workflow distribution: "+group.State+"\nEffective owners: "+group.EffectiveOwners+"\n"+group.Lineage+"\n"+string.Join("\n",rows.Select(r=>$"{r.ClashId}: {r.Status}; owner {r.Metadata.AssignedEngineer}; due {r.Metadata.DueDate:O}; comments {r.Metadata.Comments}"))};
            return new ClashGroup {GroupId=group.Key,GroupTitle=group.Title,GroupingReason=group.Reason,PrimaryOffender=group.Offender,Status=states.Count==1?states[0]:ClashStatus.Active,MaxSeverity=rows.OrderBy(r=>(int)r.Severity).First().Severity,Metadata=metadata,LevelName=group.Revision.LevelName,DisciplineA=rows[0].DisciplineA,DisciplineB=rows[0].DisciplineB,Clashes=rows};
        }
        // Phase 3 exports issue topics. Group-level mixed status and inherited ownership belong to phase 4.
        public static List<ClashGroup> IssueTopics(IEnumerable<ClashObservation> observations)=>observations.Select(row=>{
            var issue=ClashObservationAdapter.Restore(row);
            issue.Metadata.ResolutionNotes+=$"\nWorkflow: {row.Status}; priority: {row.Priority}; comparison: {row.ChangeKind}; flags: {row.ChangeFlags}"+
                $"\nOwner: {issue.Metadata.AssignedEngineer}; due: {issue.Metadata.DueDate:O}\nComments: {issue.Metadata.Comments}";
            return new ClashGroup {GroupId=row.ClashId,GroupTitle=$"{row.ClashId} · {row.CategoryNameA} / {row.CategoryNameB}",
                GroupingReason="Selected current issue",Status=issue.Status,Metadata=issue.Metadata,MaxSeverity=row.Priority=="Critical"?ClashSeverity.Critical:issue.Severity,
                DisciplineA=issue.DisciplineA,DisciplineB=issue.DisciplineB,LevelName=row.LevelName,GridRef=row.GridRef,ZoneName=row.ZoneName,Clashes=new List<ClashResult>{issue}};
        }).ToList();
    }
}
