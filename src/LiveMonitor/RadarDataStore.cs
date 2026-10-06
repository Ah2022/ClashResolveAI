using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.LiveMonitor
{
    public sealed class LiveReconcileOutcome
    {
        public int NewClashes { get; internal set; }
        public int ResolvedClashes { get; internal set; }
        public IReadOnlyList<LiveClashDto> NewRows { get; internal set; } = Array.Empty<LiveClashDto>();
    }
    // Live-only, immutable, document-scoped snapshots. Never retains engine rows.
    public sealed class RadarDataStore
    {
        public static RadarDataStore Instance { get; } = new RadarDataStore();
        private sealed class State
        {
            public List<LiveClashDto> Rows = new List<LiveClashDto>();
            public readonly HashSet<string> Detected = new HashSet<string>();
            public readonly HashSet<string> Announced = new HashSet<string>();
            public ResultTypes Types = ResultTypes.Hard | ResultTypes.PossibleHard;
            public bool ThisSession = true;
            public string Session = "";
        }
        private readonly object _lock = new object();
        private readonly Dictionary<string, State> _documents = new Dictionary<string, State>();
        private string _document = "", _session = Guid.NewGuid().ToString("N");
        private State Current {
            get { if (!_documents.TryGetValue(_document,out var state))_documents[_document]=state=new State {Session=_session};return state; }
        }
        private RadarDataStore() { }
        public event EventHandler? DataChanged;
        public void NotifyViewChanged()=>DataChanged?.Invoke(this,EventArgs.Empty);
        public void Activate(string document){lock(_lock)_document=document;NotifyViewChanged();}
        public void Close(string document){lock(_lock)_documents.Remove(document);}
        public void BeginSession(bool currentDocumentOnly=false) {
            lock(_lock){string session=Guid.NewGuid().ToString("N");if(currentDocumentOnly)Current.Session=session;else{_session=session;foreach(var state in _documents.Values)state.Session=session;}}
            NotifyViewChanged();
        }
        public string SessionId {get {lock(_lock)return Current.Session;}}
        public ResultTypes Types {get {lock(_lock)return Current.Types;}set{lock(_lock)Current.Types=value;NotifyViewChanged();}}
        public bool ThisSession {get {lock(_lock)return Current.ThisSession;}set{lock(_lock)Current.ThisSession=value;NotifyViewChanged();}}
        private static bool Active(LiveClashDto c)=>c.Status!=ClashStatus.Resolved&&c.Status!=ClashStatus.Ignored&&c.Status!=ClashStatus.Closed;
        private static bool Evaluated(ScanMode mode,LiveClashDto c)=>mode==ScanMode.HardAndClearance||c.TestType==ClashTestType.HardClash||
            (c.TestType==ClashTestType.Unverified&&c.UnverifiedReason==UnverifiedReason.SolidTest);
        private IEnumerable<LiveClashDto> Accept(IEnumerable<LiveClashDto> rows)=>rows.Where(c=>c.Origin==ResultOrigin.Live&&c.HostDocumentKey==_document);
        public void AddClashes(IEnumerable<LiveClashDto> rows)=>Reconcile(new HashSet<long>(),rows.ToList(),ScanMode.HardOnly,_=>false);
        // The scheduler supplies verified coverage, including confirmed deletions.
        // Unknown/failed/omitted coverage never turns absence into resolution.
        public LiveReconcileOutcome Reconcile(ISet<long> changed,IReadOnlyList<LiveClashDto> found,ScanMode mode,Func<LiveClashDto,bool> verifiedCoverage) {
            var outcome=new LiveReconcileOutcome();var newRows=new List<LiveClashDto>();
            lock(_lock) {
                var state=Current;var map=state.Rows.ToDictionary(c=>c.ClashKey);
                var incoming=Accept(found).GroupBy(c=>c.ClashKey).ToDictionary(g=>g.Key,g=>g.Last());
                foreach(var old in state.Rows) {
                    if(incoming.ContainsKey(old.ClashKey))continue;
                    if(!Active(old)||!Evaluated(mode,old)||!old.InvolvesHost(changed))continue;
                    if(verifiedCoverage(old)){map[old.ClashKey]=old.WithStatus(ClashStatus.Resolved);outcome.ResolvedClashes++;}
                    else map[old.ClashKey]=old.WithVerification(LiveVerificationState.Unverified,"This pair was not reliably rechecked");
                }
                foreach(var row in incoming.Values) {
                    var replacement=row;
                    if(map.TryGetValue(row.ClashKey,out var old))replacement=row.PreserveLifecycle(old);
                    // A recurring stable pair is reactivated, not announced as a
                    // newly discovered pair every time Undo/Redo restores it.
                    if(!state.Announced.Contains(row.ClashKey)&&Active(replacement)&&replacement.Verification==LiveVerificationState.Verified){newRows.Add(replacement);outcome.NewClashes++;state.Announced.Add(row.ClashKey);}
                    map[row.ClashKey]=replacement;state.Detected.Add(row.ClashKey);
                }
                state.Rows=map.Values.ToList();outcome.NewRows=newRows.AsReadOnly();
            }
            NotifyViewChanged();return outcome;
        }
        public void ReplaceSnapshot(IEnumerable<LiveClashDto> rows) {
            lock(_lock){Current.Rows=Accept(rows).ToList();foreach(var row in Current.Rows)Current.Detected.Add(row.ClashKey);}
            NotifyViewChanged();
        }
        public void IgnoreClash(LiveClashDto clash) {
            lock(_lock)Current.Rows=Current.Rows.Select(row=>row.ClashKey==clash.ClashKey?row.WithStatus(ClashStatus.Ignored):row).ToList();
            NotifyViewChanged();
        }
        public void MarkStale(string document,ISet<long>? changed,string reason) {
            lock(_lock)if(_documents.TryGetValue(document,out var state))
                state.Rows=state.Rows.Select(row=>Active(row)&&(changed==null||row.InvolvesHost(changed))?row.WithVerification(LiveVerificationState.Stale,reason):row).ToList();
            NotifyViewChanged();
        }
        public int PurgeNonHard(){int count;lock(_lock)count=Current.Rows.RemoveAll(c=>c.TestType!=ClashTestType.HardClash);NotifyViewChanged();return count;}
        public void Clear(){lock(_lock)_documents[_document]=new State {Session=_session};NotifyViewChanged();}
        public List<LiveClashDto> GetActive(){lock(_lock)return Current.Rows.Where(Active).ToList();}
        public List<LiveClashDto> GetAll(){lock(_lock)return Current.Rows.ToList();}
        public List<LiveClashDto> GetVisible(){lock(_lock)return Current.Rows.Where(c=>Active(c)&&(Current.Types&c.ResultType)!=0&&(!Current.ThisSession||c.LiveSessionId==Current.Session)).ToList();}
        public int TotalDetected {get {lock(_lock)return Current.Detected.Count;}}
        public int ActiveCount {get {lock(_lock)return Current.Rows.Count(Active);}}
        public List<string> GetCategories()=>GetVisible().SelectMany(c=>new[]{c.CategoryNameA,c.CategoryNameB}).Where(c=>!string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c=>c).ToList();
    }
}
