using ClashResolveAI.Core;
using System;

namespace ClashResolveAI.LiveMonitor
{
    public enum LiveVerificationState { Verified, Stale, Unverified }
    public readonly struct LivePoint
    {
        public double X { get; } public double Y { get; } public double Z { get; }
        public LivePoint(double x,double y,double z){X=x;Y=y;Z=z;}
    }
    public sealed class LiveElementIdentity
    {
        public long ElementId { get; }
        public string UniqueId { get; }
        public string DocumentKey { get; }
        public string LinkInstanceUniqueId { get; }
        public string VersionGuid { get; }
        public LiveElementIdentity(long id,string uid,string documentKey,string linkInstance="",string versionGuid="")
        {ElementId=id;UniqueId=uid;DocumentKey=documentKey;LinkInstanceUniqueId=linkInstance;VersionGuid=versionGuid;}
        internal string StableKey => Part(DocumentKey)+Part(LinkInstanceUniqueId)+Part(UniqueId);
        internal static string Part(string value)=>value.Length+":"+value;
    }
    // Immutable UI boundary. No model handles, API coordinate types, mutable
    // metadata collections or engine results are retained here.
    public sealed class LiveClashDto
    {
        public string HostDocumentKey { get; }
        public long SessionGeneration { get; private set; }
        public long GeometryRevision { get; }
        public string EnvironmentStamp { get; }
        public string LiveSessionId { get; }
        public ResultOrigin Origin { get; }
        public string ClashKey { get; }
        public string NormalizedKey=>ClashKey;
        public string LegacyPairKey { get; }
        public string ClashId { get; private set; }
        public LiveElementIdentity EndpointA { get; }
        public LiveElementIdentity EndpointB { get; }
        public long ElementAId=>EndpointA.ElementId;
        public long ElementBId=>EndpointB.ElementId;
        public string LinkInstanceA=>EndpointA.LinkInstanceUniqueId;
        public string LinkInstanceB=>EndpointB.LinkInstanceUniqueId;
        public Discipline DisciplineA { get; }
        public Discipline DisciplineB { get; }
        public string CategoryNameA { get; }
        public string CategoryNameB { get; }
        public string LinkFileA { get; }
        public string LinkFileB { get; }
        public string SystemTypeA { get; }
        public string SystemTypeB { get; }
        public ClashTestType TestType { get; }
        public UnverifiedReason UnverifiedReason { get; }
        public ClashSeverity Severity { get; }
        public double GapMM { get; }
        public double RequiredClearanceMM { get; }
        public double OverlapVolumeMM3 { get; }
        public LivePoint ClashPoint { get; }
        public string LevelName { get; }
        public string GridRef { get; }
        public string ZoneName { get; }
        public string LocationText { get; }
        public string GeometryEvidence { get; }
        public string RuleApplied { get; }
        public string Priority { get; }
        public ClashStatus Status { get; private set; }
        public DateTime DetectedAt { get; private set; }
        public LiveVerificationState Verification { get; private set; }
        public string VerificationReason { get; private set; }
        public ResultTypes ResultType=>TestType==ClashTestType.HardClash?ResultTypes.Hard:TestType==ClashTestType.ClearanceClash?ResultTypes.Clearance:
            UnverifiedReason==UnverifiedReason.SolidTest?ResultTypes.PossibleHard:ResultTypes.Unverified;

        // Called only by API-boundary capture (or plain-data test fixtures).
        internal LiveClashDto(ClashResult row,LiveElementIdentity a,LiveElementIdentity b,LivePoint point,string environmentStamp)
        {
            HostDocumentKey=row.HostDocumentKey;SessionGeneration=row.SessionGeneration;GeometryRevision=row.GeometryRevision;
            EnvironmentStamp=environmentStamp;LiveSessionId=row.LiveSessionId;Origin=row.Origin;EndpointA=a;EndpointB=b;
            string first=a.StableKey,second=b.StableKey;
            if(StringComparer.Ordinal.Compare(first,second)>0){var temp=first;first=second;second=temp;}
            ClashKey=LiveElementIdentity.Part(HostDocumentKey)+LiveElementIdentity.Part(first)+LiveElementIdentity.Part(second);
            LegacyPairKey=row.NormalizedKey;ClashId=row.ClashId;
            DisciplineA=row.DisciplineA;DisciplineB=row.DisciplineB;CategoryNameA=row.CategoryNameA;CategoryNameB=row.CategoryNameB;
            LinkFileA=row.LinkFileA;LinkFileB=row.LinkFileB;SystemTypeA=row.SystemTypeA;SystemTypeB=row.SystemTypeB;
            TestType=row.TestType;UnverifiedReason=row.UnverifiedReason;Severity=row.Severity;GapMM=row.GapMM;
            RequiredClearanceMM=row.RequiredClearanceMM;OverlapVolumeMM3=row.OverlapVolumeMM3;ClashPoint=point;
            LevelName=row.LevelName;GridRef=row.GridRef;ZoneName=row.ZoneName;LocationText=row.LocationText;GeometryEvidence=row.GeometryEvidence;
            RuleApplied=row.RuleApplied;Priority=row.Priority;Status=row.Status;DetectedAt=row.DetectedAt;
            Verification=TestType==ClashTestType.Unverified?LiveVerificationState.Unverified:LiveVerificationState.Verified;
            VerificationReason=Verification==LiveVerificationState.Unverified?row.GeometryEvidence:"";
        }
        public bool InvolvesHost(System.Collections.Generic.ISet<long> ids)=>
            (LinkInstanceA==""&&ids.Contains(ElementAId))||(LinkInstanceB==""&&ids.Contains(ElementBId));
        internal LiveClashDto WithStatus(ClashStatus status){var copy=(LiveClashDto)MemberwiseClone();copy.Status=status;return copy;}
        internal LiveClashDto WithVerification(LiveVerificationState state,string reason){var copy=(LiveClashDto)MemberwiseClone();copy.Verification=state;copy.VerificationReason=reason;return copy;}
        internal LiveClashDto PreserveLifecycle(LiveClashDto old){var copy=(LiveClashDto)MemberwiseClone();copy.ClashId=old.ClashId;copy.DetectedAt=old.DetectedAt;copy.Status=old.Status==ClashStatus.Resolved?ClashStatus.Active:old.Status;return copy;}
    }
}
