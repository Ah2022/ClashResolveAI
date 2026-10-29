using ClashResolveAI.Dashboard.Domain;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Dashboard.Application
{
    public interface IScanComparisonService { ScanComparison Compare(ScanSnapshot? previous,ScanSnapshot current); }
    public sealed class ScanComparisonService : IScanComparisonService
    {
        public static bool SameConfiguration(ScanCapture previous,ScanCapture current)
        {
            if(previous.IdentityVersion!=current.IdentityVersion||previous.EngineVersion!=current.EngineVersion)return false;
            // Unavailable fingerprints cannot prove comparable evaluation settings.
            return !string.IsNullOrEmpty(previous.ConfigurationFingerprint)&&previous.ConfigurationFingerprint==current.ConfigurationFingerprint;
        }
        public static bool SameEndpoints(ClashObservation a,ClashObservation b)
        {
            string Endpoint(ClashObservation c,bool first)=> (first?c.LinkInstanceA:c.LinkInstanceB)+":"+(first?c.ElementAId:c.ElementBId);
            string Unique(ClashObservation c,bool first)=>first?c.ElementUniqueIdA:c.ElementUniqueIdB;
            foreach(bool side in new[]{true,false}) {
                bool other=Endpoint(a,side)==Endpoint(b,true);
                if(Endpoint(a,side)!=Endpoint(b,other))return false;
                string ua=Unique(a,side),ub=Unique(b,other);
                if(ua!=""&&ub!=""&&ua!=ub)return false;
            }
            return true;
        }
        public static bool LinksComparable(ClashObservation row,ScanCapture previous,ScanCapture current)
        {
            foreach(string link in new[]{row.LinkInstanceA,row.LinkInstanceB}.Where(l=>l!="")) {
                JObject? Find(string json)=>JArray.Parse(json).OfType<JObject>().FirstOrDefault(l=>(string?)l["InstanceUniqueId"]==link);
                var a=Find(previous.LinkedModelsJson);var b=Find(current.LinkedModelsJson);
                if(a==null||b==null||(bool?)a["IsLoaded"]!=true||(bool?)b["IsLoaded"]!=true)return false;
                if((string?)a["DocumentKey"]!=(string?)b["DocumentKey"]||!JToken.DeepEquals(a["Transform"],b["Transform"]))return false;
            }
            return true;
        }
        public ScanComparison Compare(ScanSnapshot? previous,ScanSnapshot current)
        {
            if(previous!=null&&(previous.Version.Capture.DocumentKey!=current.Version.Capture.DocumentKey||previous.Version.SequenceNumber>=current.Version.SequenceNumber))
                throw new InvalidOperationException("Select an earlier scan from the same document lineage.");
            bool config=previous==null||SameConfiguration(previous.Version.Capture,current.Version.Capture);
            var old=(previous?.Issues??Array.Empty<ClashObservation>()).ToDictionary(c=>c.NormalizedKey);
            var changes=new List<IssueChange>();
            foreach(var row in current.Issues.OrderBy(c=>c.ClashId,StringComparer.Ordinal)) {
                old.TryGetValue(row.NormalizedKey,out var before);
                if(before!=null&&!SameEndpoints(before,row))throw new InvalidOperationException("Element identity was replaced. An explicit issue identity migration is required.");
                var change=new IssueChange {ClashId=row.ClashId,Kind=ClashChangeKind.Persistent,Reason=row.EvaluationReason};
                if(!config)change.Flags|=ClashChangeFlags.ConfigurationChanged;
                if(row.ObservationKind==ScanObservationKind.NotEvaluated||row.ObservationKind==ScanObservationKind.LegacyImported)change.Kind=ClashChangeKind.NotEvaluated;
                else if(row.ObservationKind==ScanObservationKind.VerifiedAbsent) {
                    if(previous!=null&&before!=null&&config&&LinksComparable(row,previous.Version.Capture,current.Version.Capture))
                        change.Kind=before.ObservationKind==ScanObservationKind.VerifiedAbsent?ClashChangeKind.UnchangedResolved:ClashChangeKind.Resolved;
                    else {change.Kind=ClashChangeKind.NotEvaluated;change.Reason="Absence cannot establish resolution without comparable earlier coverage/configuration.";}
                } else if(before==null) {
                    change.Kind=previous==null?ClashChangeKind.New:
                        row.ReopenedAtUtc>previous.Version.EndedAtUtc?ClashChangeKind.Reopened:ClashChangeKind.New;
                    if(previous!=null&&change.Kind!=ClashChangeKind.Reopened&&
                        ((row.SeenInScanCount>1&&(row.TimestampKind!="Utc"||row.DetectedAt<=previous.Version.EndedAtUtc))||
                        (row.TimestampKind!="Utc"&&row.DetectedAtOriginalText!=""))){
                        change.Kind=ClashChangeKind.NotEvaluated;change.Reason="Known issue was outside the selected baseline; first occurrence cannot be established by this comparison.";
                    }
                }
                else if(row.TestType!="Unverified"&&(before.ObservationKind==ScanObservationKind.VerifiedAbsent||before.Status=="Resolved"||(row.ReopenedAtUtc.HasValue&&(!before.ReopenedAtUtc.HasValue||row.ReopenedAtUtc.Value>before.ReopenedAtUtc.Value))))change.Kind=ClashChangeKind.Reopened;
                if(before!=null&&row.ObservationKind==ScanObservationKind.Observed&&before.ObservationKind==ScanObservationKind.Observed)change.Flags|=EvidenceChanges(before,row);
                changes.Add(change);
            }
            var present=new HashSet<string>(current.Issues.Select(c=>c.NormalizedKey));
            foreach(var row in old.Values.Where(c=>!present.Contains(c.NormalizedKey)))changes.Add(new IssueChange {ClashId=row.ClashId,Kind=ClashChangeKind.MissingFromScan,Reason="No current observation or reliable absence record; resolution is not established."});
            return new ScanComparison {PreviousScanId=previous?.Version.ScanId??"",CurrentScanId=current.Version.ScanId,InitialBaseline=previous==null,
                CoverageNotice=previous==null?"Initial completed baseline":!config?"Evaluation settings changed; absence is not counted as geometry resolution":previous.Version.Capture.ScopeJson!=current.Version.Capture.ScopeJson?"Scopes differ; only reliably evaluated issues are comparable":"Comparable evaluation settings; per-issue coverage still applies",
                Issues=changes.OrderBy(c=>c.ClashId,StringComparer.Ordinal).ToList()};
        }
        private static ClashChangeFlags EvidenceChanges(ClashObservation a,ClashObservation b)
        {
            bool reversed=a.ElementAId==b.ElementBId&&a.LinkInstanceA==b.LinkInstanceB;
            string signatureA=reversed?b.ElementSignatureB:b.ElementSignatureA,signatureB=reversed?b.ElementSignatureA:b.ElementSignatureB;
            var flags=ClashChangeFlags.None;
            int Rank(string severity)=>severity=="Critical"?0:severity=="Hard"?1:severity=="Soft"?2:severity=="Clearance"?3:99;
            if(Rank(b.Severity)<Rank(a.Severity))flags|=ClashChangeFlags.SeverityIncreased;
            if(Rank(b.Severity)>Rank(a.Severity))flags|=ClashChangeFlags.SeverityDecreased;
            if(a.TestType!=b.TestType)flags|=ClashChangeFlags.ClassificationChanged;
            bool Diff(double x,double y,double tolerance)=>Math.Abs(x-y)>tolerance;
            if(Diff(a.GapMm,b.GapMm,0.1)||Diff(a.OverlapVolumeMm3,b.OverlapVolumeMm3,1)||Diff(a.X,b.X,0.1/304.8)||Diff(a.Y,b.Y,0.1/304.8)||Diff(a.Z,b.Z,0.1/304.8)
                ||(a.ElementSignatureA!=""&&signatureA!=""&&a.ElementSignatureA!=signatureA)
                ||(a.ElementSignatureB!=""&&signatureB!=""&&a.ElementSignatureB!=signatureB))flags|=ClashChangeFlags.GeometryChanged;
            return flags;
        }
    }
}
