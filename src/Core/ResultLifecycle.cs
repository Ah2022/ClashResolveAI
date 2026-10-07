using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Core
{
    // Actual source coverage, captured by the engine after level/zone/category exclusions.
    // IDs stay partitioned by link instance; host and linked numeric IDs may coincide.
    public sealed class ScanScope
    {
        private readonly string _document;
        private readonly HashSet<long> _unreliableHost=new HashSet<long>();
        private readonly HashSet<string> _missingPairs=new HashSet<string>();
        private readonly Dictionary<string,HashSet<long>> _sources=new Dictionary<string,HashSet<long>>();
        private readonly Dictionary<string,HashSet<string>> _targets=new Dictionary<string,HashSet<string>>();
        internal string PublishedScanId="";
        internal int PublishedRetainedClearance=0;
        public object Snapshot() => new {
            DocumentKey=_document,
            Sources=_sources.OrderBy(p=>p.Key,StringComparer.Ordinal).ToDictionary(p=>p.Key,p=>p.Value.OrderBy(id=>id).ToArray()),
            TargetLinks=_targets.OrderBy(p=>p.Key,StringComparer.Ordinal).ToDictionary(p=>p.Key,p=>p.Value.OrderBy(k=>k,StringComparer.Ordinal).ToArray()),
            UnreliableHost=_unreliableHost.OrderBy(id=>id).ToArray(),MissingPairs=_missingPairs.OrderBy(k=>k,StringComparer.Ordinal).ToArray()
        };
        public ScanScope(string document="") { _document=document; }
        public void Note(long source,string sourceLink,string targetLink)
        {
            if(!_sources.TryGetValue(sourceLink,out var ids))_sources[sourceLink]=ids=new HashSet<long>();
            if(!_targets.TryGetValue(sourceLink,out var links))_targets[sourceLink]=links=new HashSet<string>();
            ids.Add(source);links.Add(targetLink);
        }
        private bool Covers(long id,string link,string target)=>_sources.TryGetValue(link,out var ids)&&ids.Contains(id)&&_targets[link].Contains(target);
        public void MissingPair(string key)=>_missingPairs.Add(key);
        public void MissingSource(long id,string link){if(link=="")_unreliableHost.Add(id);}
        public bool IsHostReliable(long id)=>CoversHost(id)&&!_unreliableHost.Contains(id);
        private bool ReliableCovers(long id,string link,string target)=>(link!=""||!_unreliableHost.Contains(id))&&Covers(id,link,target);
        public bool ContainsPair(string document,long a,string linkA,long b,string linkB,string legacyKey)=>
            !_missingPairs.Contains(legacyKey)&&(_document==""||_document==document)&&
            (ReliableCovers(a,linkA,linkB)||ReliableCovers(b,linkB,linkA));
        public bool CoversHost(long id)=>_sources.TryGetValue("",out var ids)&&ids.Contains(id);
        public bool IsReliable(ClashResult c)=>!_missingPairs.Contains(c.NormalizedKey);
        public bool Contains(ClashResult c)=>ContainsPair(c.HostDocumentKey,c.ElementAId,c.LinkInstanceA,c.ElementBId,c.LinkInstanceB,c.NormalizedKey);
    }
    public sealed class MergeOutcome
    {
        public List<ClashResult> Rows=new List<ClashResult>();
        public List<ClashResult> Touched=new List<ClashResult>();
        public int RetainedClearance;
    }
    public static class ResultLifecycle
    {
        public static bool Evaluated(ScanMode mode,ClashResult c)=>mode==ScanMode.HardAndClearance||c.TestType==ClashTestType.HardClash||
            (c.TestType==ClashTestType.Unverified&&c.UnverifiedReason==UnverifiedReason.SolidTest);
        public static MergeOutcome Merge(IEnumerable<ClashResult> previous,List<ClashResult> found,ScanMode mode,Func<ClashResult,bool> scope)
        {
            var incoming=found.ToDictionary(c=>c.NormalizedKey);
            var map=previous.ToDictionary(c=>c.NormalizedKey);
            var result=new MergeOutcome { Touched=new List<ClashResult>(found) };
            foreach(var old in map.Values){
                if(incoming.TryGetValue(old.NormalizedKey,out var replacement)){
                    replacement.Metadata=old.Metadata;replacement.DetectedAt=old.DetectedAt;
                    replacement.AiSuggestion=old.AiSuggestion;replacement.RfiText=old.RfiText;
                    replacement.Status=old.Status==ClashStatus.Resolved?ClashStatus.Active:old.Status;
                }else if(Evaluated(mode,old)&&scope(old)){
                    if(old.Status!=ClashStatus.Ignored&&old.Status!=ClashStatus.Closed&&old.Status!=ClashStatus.Resolved){old.Status=ClashStatus.Resolved;result.Touched.Add(old);}
                }else if(old.TestType==ClashTestType.ClearanceClash)result.RetainedClearance++;
            }
            foreach(var c in found)map[c.NormalizedKey]=c;
            result.Rows=map.Values.ToList();return result;
        }
    }
    [Flags]
    public enum ResultTypes { None=0,Hard=1,PossibleHard=2,Clearance=4,Unverified=8,All=15 }
    public sealed class ResultViewFilter
    {
        public ResultTypes Types=ResultTypes.Hard|ResultTypes.PossibleHard;
        public string Search="",Discipline="",System="",Category="",Severity="",Status="",Level="";
        public ResultViewFilter Copy()=>(ResultViewFilter)MemberwiseClone();
        public static ResultTypes Defaults(ScanMode mode)=>mode==ScanMode.HardOnly?ResultTypes.Hard|ResultTypes.PossibleHard:ResultTypes.All;
        public static ResultTypes TypeOf(ClashResult c)=>c.TestType==ClashTestType.HardClash?ResultTypes.Hard:
            c.TestType==ClashTestType.ClearanceClash?ResultTypes.Clearance:
            c.TestType==ClashTestType.Unverified&&c.UnverifiedReason==UnverifiedReason.SolidTest?ResultTypes.PossibleHard:ResultTypes.Unverified;
        public bool Matches(ClashResult c)
        {
            if((Types&TypeOf(c))==0)return false;
            string q=Search.ToUpperInvariant();
            return (q==""||new[]{c.ElementAId.ToString(),c.ElementBId.ToString(),c.SystemTypeA,c.SystemTypeB,c.LevelName,c.GridRef}.Any(s=>(s??"").ToUpperInvariant().Contains(q)))&&
                (Discipline==""||c.DisciplineA.ToString()==Discipline||c.DisciplineB.ToString()==Discipline)&&
                (System==""||c.SystemTypeA==System||c.SystemTypeB==System)&&
                (Category==""||c.CategoryNameA==Category||c.CategoryNameB==Category)&&
                (Severity==""||c.Severity.ToString()==Severity||(Severity=="Critical"&&c.Priority=="Critical"))&&
                (Status==""||c.Status.ToString().Equals(Status.Replace(" ",""),StringComparison.OrdinalIgnoreCase))&&
                (Level==""||c.LevelName==Level);
        }
    }
}
