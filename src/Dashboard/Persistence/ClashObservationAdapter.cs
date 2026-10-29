using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using ClashResolveAI.Dashboard.Domain;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Dashboard.Persistence
{
    public static class ClashObservationAdapter
    {
        public static ClashObservation Capture(ClashResult c) => new ClashObservation {
            ClashId=c.ClashId, NormalizedKey=c.NormalizedKey, DocumentKey=c.HostDocumentKey,
            GroupId=c.GroupId, DetectedAt=c.DetectedAt, DetectedAtOriginalText=c.DetectedAt.ToString("o"),TimestampKind=c.DetectedAt.Kind==DateTimeKind.Utc?"Utc":"LegacyLocalOrUnknown",
            ElementAId=c.ElementAId, ElementBId=c.ElementBId,
            ElementUniqueIdA=c.ElementUniqueIdA, ElementUniqueIdB=c.ElementUniqueIdB,
            ElementSignatureA=c.ElementSignatureA, ElementSignatureB=c.ElementSignatureB,
            LinkInstanceA=c.LinkInstanceA, LinkInstanceB=c.LinkInstanceB, LinkFileA=c.LinkFileA, LinkFileB=c.LinkFileB,
            DisciplineA=c.DisciplineA.ToString(), DisciplineB=c.DisciplineB.ToString(),
            CategoryNameA=c.CategoryNameA, CategoryNameB=c.CategoryNameB,
            SystemTypeA=c.SystemTypeA, SystemTypeB=c.SystemTypeB, FamilyTypeA=c.FamilyTypeA, FamilyTypeB=c.FamilyTypeB,
            StructuralSubTypeA=c.StructuralSubTypeA, StructuralSubTypeB=c.StructuralSubTypeB,
            TestType=c.TestType.ToString(), ClashType=c.ClashType, Severity=c.Severity.ToString(), Priority=c.Priority,
            Status=c.Status.ToString(), RuleApplied=c.RuleApplied, MovingDiscipline=c.MovingDiscipline,
            GeometryEvidence=c.GeometryEvidence, UnverifiedReason=c.UnverifiedReason.ToString(),
            GapMm=c.GapMM, OverlapVolumeMm3=c.OverlapVolumeMM3, RequiredClearanceMm=c.RequiredClearanceMM,
            X=c.ClashPoint.X, Y=c.ClashPoint.Y, Z=c.ClashPoint.Z,
            LevelName=c.LevelName, GridRef=c.GridRef, ZoneName=c.ZoneName, LocationText=c.LocationText,
            MetadataJson=JsonConvert.SerializeObject(c.Metadata), AiSuggestion=c.AiSuggestion, RfiText=c.RfiText,
            AlternativesJson=JsonConvert.SerializeObject(c.Alternatives),
            ChangeKind=Enum.TryParse(c.ChangeKind,out ClashChangeKind change)?change:(ClashChangeKind?)null,ChangeFlags=(ClashChangeFlags)c.ChangeFlags,
            ObservationKind=Parse(c.ObservationKind,ScanObservationKind.Observed),EvaluationReason=c.EvaluationReason,
            PreviousScanId=c.PreviousScanId,CurrentScanId=c.CurrentScanId,LastSeenAtUtc=c.LastSeenAtUtc,ResolvedAtUtc=c.ResolvedAtUtc,
            ReopenedAtUtc=c.ReopenedAtUtc,OpenEpisodeAtUtc=c.OpenEpisodeAtUtc,SeenInScanCount=c.SeenInScanCount,ConsecutiveScanCount=c.ConsecutiveScanCount
        };

        public static ClashResult Restore(ClashObservation c) => new ClashResult {
            ClashId=c.ClashId, HostDocumentKey=c.DocumentKey, GroupId=c.GroupId, DetectedAt=c.DetectedAt,
            ElementAId=c.ElementAId, ElementBId=c.ElementBId,
            ElementUniqueIdA=c.ElementUniqueIdA, ElementUniqueIdB=c.ElementUniqueIdB,
            ElementSignatureA=c.ElementSignatureA, ElementSignatureB=c.ElementSignatureB,
            LinkInstanceA=c.LinkInstanceA, LinkInstanceB=c.LinkInstanceB, LinkFileA=c.LinkFileA, LinkFileB=c.LinkFileB,
            DisciplineA=Parse(c.DisciplineA,Discipline.Unknown), DisciplineB=Parse(c.DisciplineB,Discipline.Unknown),
            CategoryNameA=c.CategoryNameA, CategoryNameB=c.CategoryNameB, SystemTypeA=c.SystemTypeA, SystemTypeB=c.SystemTypeB,
            FamilyTypeA=c.FamilyTypeA, FamilyTypeB=c.FamilyTypeB, StructuralSubTypeA=c.StructuralSubTypeA, StructuralSubTypeB=c.StructuralSubTypeB,
            TestType=Parse(c.TestType,ClashTestType.Unverified), ClashType=c.ClashType, Severity=Parse(c.Severity,ClashSeverity.Ignore),
            Priority=c.Priority, Status=Parse(c.Status,ClashStatus.New), RuleApplied=c.RuleApplied, MovingDiscipline=c.MovingDiscipline,
            GeometryEvidence=c.GeometryEvidence, UnverifiedReason=Parse(c.UnverifiedReason,UnverifiedReason.MissingGeometry),
            GapMM=c.GapMm, OverlapVolumeMM3=c.OverlapVolumeMm3, RequiredClearanceMM=c.RequiredClearanceMm,
            ClashPoint=new XYZ(c.X,c.Y,c.Z), LevelName=c.LevelName, GridRef=c.GridRef, ZoneName=c.ZoneName, LocationText=c.LocationText,
            Metadata=JsonConvert.DeserializeObject<ClashMetadata>(c.MetadataJson)??new ClashMetadata(),
            AiSuggestion=c.AiSuggestion, RfiText=c.RfiText,
            Alternatives=JsonConvert.DeserializeObject<List<RoutingAlternative>>(c.AlternativesJson)??new List<RoutingAlternative>(),
            // A restored observation must be reverified in the current Revit session before export/navigation.
            GeometryRevision=-1, Origin=ResultOrigin.Full,
            ChangeKind=c.ChangeKind?.ToString()??"",ChangeFlags=(int)c.ChangeFlags,PreviousScanId=c.PreviousScanId,CurrentScanId=c.CurrentScanId,
            ObservationKind=c.ObservationKind.ToString(),EvaluationReason=c.EvaluationReason,
            LastSeenAtUtc=c.LastSeenAtUtc,ResolvedAtUtc=c.ResolvedAtUtc,ReopenedAtUtc=c.ReopenedAtUtc,OpenEpisodeAtUtc=c.OpenEpisodeAtUtc,
            SeenInScanCount=c.SeenInScanCount,ConsecutiveScanCount=c.ConsecutiveScanCount
        };

        private static T Parse<T>(string value,T fallback) where T:struct => Enum.TryParse(value,out T parsed)?parsed:fallback;

        public static List<GroupScanRevision> CaptureGroups(IEnumerable<ClashGroup> groups)
        {
            return groups.Select(g=>new GroupScanRevision {
                // Exact membership key freezes this scan's grouping; cross-scan split/merge matching is Phase 4.
                GroupKey=HistoryHash.Of(g.GroupingReason+"|"+string.Join("|",g.Clashes.Select(c=>c.NormalizedKey).OrderBy(k=>k,StringComparer.Ordinal))),
                Title=g.GroupTitle, Reason=g.GroupingReason, PrimaryOffender=g.PrimaryOffender,
                Status=g.Status.ToString(),MaxSeverity=g.MaxSeverity.ToString(),LevelName=g.LevelName,GridRef=g.GridRef,ZoneName=g.ZoneName,
                DisciplineA=g.DisciplineA.ToString(),DisciplineB=g.DisciplineB.ToString(),
                MetadataJson=JsonConvert.SerializeObject(g.Metadata), MemberClashIds=g.Clashes.Select(c=>c.ClashId).OrderBy(k=>k,StringComparer.Ordinal).ToList()
            }).ToList();
        }
    }
}
