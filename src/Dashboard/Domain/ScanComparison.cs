using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Dashboard.Domain
{
    public sealed class ScanSnapshot
    {
        public ScanVersion Version { get; set; } = new ScanVersion();
        public IReadOnlyList<ClashObservation> Issues { get; set; } = Array.Empty<ClashObservation>();
    }
    public sealed class IssueChange
    {
        public string ClashId { get; set; } = "";
        public ClashChangeKind Kind { get; set; }
        public ClashChangeFlags Flags { get; set; }
        public string Reason { get; set; } = "";
    }
    public sealed class ScanComparison
    {
        public string PreviousScanId { get; set; } = "";
        public string CurrentScanId { get; set; } = "";
        public bool InitialBaseline { get; set; }
        public string CoverageNotice { get; set; } = "";
        public IReadOnlyList<IssueChange> Issues { get; set; } = Array.Empty<IssueChange>();
        public int Count(ClashChangeKind kind)=>Issues.Count(i=>i.Kind==kind);
        public int FlagCount(ClashChangeFlags flag)=>Issues.Count(i=>(i.Flags&flag)!=0);
    }
    public sealed class DashboardMetrics
    {
        public int Open { get; set; }
        public int Critical { get; set; }
        public int New { get; set; }
        public int Reopened { get; set; }
        public int Resolved { get; set; }
        public int Unverified { get; set; }
        public int Stale { get; set; }
        public int ActionableGroups { get; set; }
        public double? Health { get; set; }
        public int HealthBasis { get; set; }
        public DateTime? LastScanUtc { get; set; }
    }
    public sealed class IssueMutation
    {
        public ClashObservation Snapshot { get; set; } = new ClashObservation();
        public string ExpectedStatus { get; set; } = "";
        public string ExpectedMetadataJson { get; set; } = "{}";
        public string Origin { get; set; } = "User";
        public string Author { get; set; } = "";
        public string Reason { get; set; } = "";
    }
}
