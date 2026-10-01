using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.LiveMonitor
{
    // Live-only, document-scoped session history. Never writes the Full Scan database.
    // Plain data only: safe for the radar's WPF dispatcher to read.
    public sealed class RadarDataStore
    {
        public static RadarDataStore Instance { get; } = new RadarDataStore();
        private sealed class State
        {
            public List<ClashResult> Rows = new List<ClashResult>();
            public readonly HashSet<string> Detected = new HashSet<string>();
            public ResultTypes Types = ResultTypes.Hard | ResultTypes.PossibleHard;
            public bool ThisSession = true;
            public string Session = "";
        }
        private readonly object _lock = new object();
        private readonly Dictionary<string, State> _documents = new Dictionary<string, State>();
        private string _document = "", _session = Guid.NewGuid().ToString("N");
        private State Current
        {
            get {
                if (!_documents.TryGetValue(_document, out var state))
                    _documents[_document] = state = new State { Session = _session };
                return state;
            }
        }
        private RadarDataStore() { }
        public event EventHandler? DataChanged;
        public void NotifyViewChanged() => DataChanged?.Invoke(this, EventArgs.Empty);
        public void Activate(string document) { lock (_lock) _document = document; NotifyViewChanged(); }
        public void Close(string document) { lock (_lock) _documents.Remove(document); }
        public void BeginSession(bool currentDocumentOnly = false)
        {
            lock (_lock) {
                string session = Guid.NewGuid().ToString("N");
                if (currentDocumentOnly) Current.Session = session;
                else { _session = session; foreach (var state in _documents.Values) state.Session = session; }
            }
            NotifyViewChanged();
        }
        public string SessionId { get { lock (_lock) return Current.Session; } }
        public ResultTypes Types { get { lock (_lock) return Current.Types; } set { lock (_lock) Current.Types = value; NotifyViewChanged(); } }
        public bool ThisSession { get { lock (_lock) return Current.ThisSession; } set { lock (_lock) Current.ThisSession = value; NotifyViewChanged(); } }
        private static bool Active(ClashResult c) => c.Status != ClashStatus.Resolved && c.Status != ClashStatus.Ignored && c.Status != ClashStatus.Closed;
        private IEnumerable<ClashResult> Accept(IEnumerable<ClashResult> rows) => rows.Where(c => c.Origin == ResultOrigin.Live && c.HostDocumentKey == _document);
        public void AddClashes(IEnumerable<ClashResult> incoming)
        {
            lock (_lock) {
                var state = Current;
                state.Rows = ResultLifecycle.Merge(state.Rows, Accept(incoming).ToList(), ScanMode.HardOnly, _ => false).Rows;
                foreach (var c in state.Rows) state.Detected.Add(c.NormalizedKey);
            }
            NotifyViewChanged();
        }
        public void Reconcile(ISet<long> changed, List<ClashResult> found, ScanMode mode, ScanScope? scope = null)
        {
            lock (_lock) {
                var state = Current;
                state.Rows = ResultLifecycle.Merge(state.Rows, Accept(found).ToList(), mode,
                    c => c.InvolvesHost(changed) && (scope == null || scope.IsReliable(c))).Rows;
                foreach (var c in state.Rows) state.Detected.Add(c.NormalizedKey);
            }
            NotifyViewChanged();
        }
        public void ReplaceSnapshot(IEnumerable<ClashResult> rows)
        {
            lock (_lock) {
                Current.Rows = Accept(rows).ToList();
                foreach (var c in Current.Rows) Current.Detected.Add(c.NormalizedKey);
            }
            NotifyViewChanged();
        }
        public void IgnoreClash(ClashResult clash)
        {
            lock (_lock) {
                var row = Current.Rows.FirstOrDefault(c => c.NormalizedKey == clash.NormalizedKey);
                if (row != null) row.Status = ClashStatus.Ignored;
            }
            NotifyViewChanged();
        }
        public int PurgeNonHard()
        {
            int count;
            lock (_lock) count = Current.Rows.RemoveAll(c => c.TestType != ClashTestType.HardClash);
            NotifyViewChanged(); return count;
        }
        public void Clear() { lock (_lock) _documents[_document] = new State { Session = _session }; NotifyViewChanged(); }
        public List<ClashResult> GetActive() { lock (_lock) return Current.Rows.Where(Active).ToList(); }
        public List<ClashResult> GetVisible()
        {
            lock (_lock) return Current.Rows.Where(c => Active(c) && (Current.Types & ResultViewFilter.TypeOf(c)) != 0 &&
                (!Current.ThisSession || c.LiveSessionId == Current.Session)).ToList();
        }
        public int TotalDetected { get { lock (_lock) return Current.Detected.Count; } }
        public int ActiveCount { get { lock (_lock) return Current.Rows.Count(Active); } }
        public List<string> GetCategories() => GetVisible().SelectMany(c => new[] { c.CategoryNameA, c.CategoryNameB })
            .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
    }
}
