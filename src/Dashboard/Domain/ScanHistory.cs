using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ClashResolveAI.Dashboard.Domain
{
    // Persistence DTOs contain only copied values. No Revit API objects belong here.
    public enum ScanVersionState { Running, Completed, Cancelled, Failed, LegacyImported }
    public enum ScanObservationKind { Observed, VerifiedAbsent, NotEvaluated, LegacyImported }
    public enum ClashChangeKind { New, Persistent, Resolved, Reopened, NotEvaluated, MissingFromScan, UnchangedResolved }
    [Flags]
    public enum ClashChangeFlags { None=0, GeometryChanged=1, SeverityIncreased=2, SeverityDecreased=4, ClassificationChanged=8, ConfigurationChanged=16 }

    public sealed class ScanCapture
    {
        public string DocumentKey { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string DocumentIdentityJson { get; set; } = "{}";
        public string ModelFingerprint { get; set; } = "";
        public string ModelFingerprintKind { get; set; } = "VersionAndRevision";
        public string RuleSetName { get; set; } = "";
        public string ConfigurationFingerprint { get; set; } = "";
        public string ConfigurationJson { get; set; } = "{}";
        public string ScopeJson { get; set; } = "{}";
        public string LinkedModelsJson { get; set; } = "[]";
        public string ScanMode { get; set; } = "";
        public string EngineVersion { get; set; } = "";
        public string IdentityVersion { get; set; } = "pair-v1";
        public bool FullModelScope { get; set; }
    }

    public sealed class ScanVersion
    {
        public string ScanId { get; set; } = "";
        public int SequenceNumber { get; set; }
        public ScanCapture Capture { get; set; } = new ScanCapture();
        public DateTime StartedAtUtc { get; set; }
        public DateTime? EndedAtUtc { get; set; }
        public ScanVersionState State { get; set; }
        public string Completeness { get; set; } = "Pending";
        public string FailureReason { get; set; } = "";
        public string StatisticsJson { get; set; } = "{}";
        public string CoverageJson { get; set; } = "{}";
    }

    public sealed class ClashObservation
    {
        public string ClashId { get; set; } = "";
        public string NormalizedKey { get; set; } = "";
        public string DocumentKey { get; set; } = "";
        public string GroupId { get; set; } = "";
        public DateTime DetectedAt { get; set; }
        public string TimestampKind { get; set; } = "Utc";
        public string DetectedAtOriginalText { get; set; } = "";
        public long ElementAId { get; set; }
        public long ElementBId { get; set; }
        public string ElementUniqueIdA { get; set; } = "";
        public string ElementUniqueIdB { get; set; } = "";
        public string ElementSignatureA { get; set; } = "";
        public string ElementSignatureB { get; set; } = "";
        public string LinkInstanceA { get; set; } = "";
        public string LinkInstanceB { get; set; } = "";
        public string LinkFileA { get; set; } = "";
        public string LinkFileB { get; set; } = "";
        public string DisciplineA { get; set; } = "Unknown";
        public string DisciplineB { get; set; } = "Unknown";
        public string CategoryNameA { get; set; } = "";
        public string CategoryNameB { get; set; } = "";
        public string SystemTypeA { get; set; } = "";
        public string SystemTypeB { get; set; } = "";
        public string FamilyTypeA { get; set; } = "";
        public string FamilyTypeB { get; set; } = "";
        public string StructuralSubTypeA { get; set; } = "";
        public string StructuralSubTypeB { get; set; } = "";
        public string TestType { get; set; } = "Unverified";
        public string ClashType { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Priority { get; set; } = "";
        public string Status { get; set; } = "New";
        public string RuleApplied { get; set; } = "";
        public string MovingDiscipline { get; set; } = "";
        public string GeometryEvidence { get; set; } = "";
        public string UnverifiedReason { get; set; } = "";
        public double GapMm { get; set; }
        public double OverlapVolumeMm3 { get; set; }
        public double RequiredClearanceMm { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string LevelName { get; set; } = "";
        public string GridRef { get; set; } = "";
        public string ZoneName { get; set; } = "";
        public string LocationText { get; set; } = "";
        public string MetadataJson { get; set; } = "{}";
        public string AiSuggestion { get; set; } = "";
        public string RfiText { get; set; } = "";
        public string AlternativesJson { get; set; } = "[]";
        public ScanObservationKind ObservationKind { get; set; } = ScanObservationKind.Observed;
        public string EvaluationReason { get; set; } = "";
        public ClashChangeKind? ChangeKind { get; set; }
        public ClashChangeFlags ChangeFlags { get; set; }
        public string PreviousScanId { get; set; } = "";
        public string CurrentScanId { get; set; } = "";
        public DateTime? LastSeenAtUtc { get; set; }
        public DateTime? ResolvedAtUtc { get; set; }
        public DateTime? ReopenedAtUtc { get; set; }
        public DateTime? OpenEpisodeAtUtc { get; set; }
        public int SeenInScanCount { get; set; }
        public int ConsecutiveScanCount { get; set; }
    }

    public sealed class IssueScanRecord
    {
        public string ScanId { get; set; }="";
        public ClashObservation Observation { get; set; }=new ClashObservation();
    }
    public sealed class ClashLifecycleEvent
    {
        public string EventId { get; set; } = "";
        public string ClashId { get; set; } = "";
        public string? ScanId { get; set; }
        public string FromStatus { get; set; } = "";
        public string ToStatus { get; set; } = "";
        public string Origin { get; set; } = "";
        public string Author { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string TimestampKind { get; set; } = "Utc";
        public string Comment { get; set; } = "";
    }

    public sealed class GroupScanRevision
    {
        public string CoordinationJson {get;set;}="{}";
        public string GroupKey { get; set; } = "";
        public string Title { get; set; } = "";
        public string Reason { get; set; } = "";
        public string PrimaryOffender { get; set; } = "";
        public string Status { get; set; } = "";
        public string MaxSeverity { get; set; } = "";
        public string LevelName { get; set; } = "";
        public string GridRef { get; set; } = "";
        public string ZoneName { get; set; } = "";
        public string DisciplineA { get; set; } = "";
        public string DisciplineB { get; set; } = "";
        public string MetadataJson { get; set; } = "{}";
        public List<string> MemberClashIds { get; set; } = new List<string>();
    }

    public static class HistoryHash
    {
        public static string Of(string value)
        {
            using var hash=SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }
    }
}
