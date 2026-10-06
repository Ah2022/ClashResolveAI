using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.LiveMonitor
{
    internal enum LiveOperation { Navigate3D, Navigate2D, Refresh, Clear, Cancel, Inspect, Pin, Snapshot, Selection, Advance, Mode, Pane }

    internal sealed class LiveSessionRegistry
    {
        private static long _nextGeneration;
        private readonly Dictionary<string,long> _sessions = new Dictionary<string,long>();
        public string ActiveKey { get; private set; } = "";
        public long Generation(string key) => _sessions.TryGetValue(key,out var generation)?generation:0;
        public bool IsCurrent(string key,long generation) => key!="" && ActiveKey==key && generation!=0 && Generation(key)==generation;
        public long Renew(string key) => _sessions[key]=System.Threading.Interlocked.Increment(ref _nextGeneration);
        public string Activate(string key) {
            if(key==ActiveKey)return "";
            string previous=ActiveKey;
            if(previous!="")Renew(previous);
            ActiveKey=key;
            if(key!="" && !_sessions.ContainsKey(key))Renew(key);
            return previous;
        }
        public void Close(string key) {_sessions.Remove(key);if(ActiveKey==key)ActiveKey="";}
        public void Clear() {_sessions.Clear();ActiveKey="";}
    }

    // No Revit handles: identity is captured when submitted, never from the active
    // document when executed. Geometry revision is separate from session generation.
    internal sealed class LiveRequest
    {
        public Guid RequestId { get; } = Guid.NewGuid();
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public string DocumentKey { get; }
        public long SessionGeneration { get; }
        public LiveOperation Operation { get; }
        public string ClashKey { get; }
        public long GeometryRevision { get; }
        public object? Payload { get; }
        public LiveRequest(string documentKey, long generation, LiveOperation operation,
            string clashKey = "", long geometryRevision = 0, object? payload = null)
        { DocumentKey=documentKey; SessionGeneration=generation; Operation=operation;
          ClashKey=clashKey; GeometryRevision=geometryRevision; Payload=payload; }
    }

    internal sealed class LiveRequestQueue
    {
        private readonly object _gate = new object();
        private readonly LinkedList<LiveRequest> _items = new LinkedList<LiveRequest>();
        private readonly int _capacity;
        public long Coalesced { get; private set; }
        public int Count { get { lock(_gate) return _items.Count; } }
        public LiveRequestQueue(int capacity = 128) { _capacity=capacity; }
        private static bool Barrier(LiveOperation op) => op==LiveOperation.Clear || op==LiveOperation.Cancel || op==LiveOperation.Pin || op==LiveOperation.Mode;
        private static int Family(LiveOperation op) => op==LiveOperation.Navigate2D || op==LiveOperation.Navigate3D ? -1 : (int)op;
        public bool Enqueue(LiveRequest request)
        {
            lock(_gate) {
                if(!Barrier(request.Operation)) {
                    // Replacement never crosses an explicit control/save barrier.
                    for(var node=_items.Last;node!=null;node=node.Previous) {
                        var old=node.Value;
                        if(Barrier(old.Operation)) break;
                        if(old.DocumentKey==request.DocumentKey && old.SessionGeneration==request.SessionGeneration && Family(old.Operation)==Family(request.Operation)) {
                            _items.Remove(node); Coalesced++; break;
                        }
                    }
                }
                if(_items.Count>=_capacity) return false;
                _items.AddLast(request); return true;
            }
        }
        public LiveRequest? Dequeue() { lock(_gate) { if(_items.First==null)return null;var value=_items.First.Value;_items.RemoveFirst();return value; } }
        public void RemoveDocument(string key) { lock(_gate) { for(var n=_items.First;n!=null;) { var next=n.Next;if(n.Value.DocumentKey==key)_items.Remove(n);n=next; } } }
        public void Clear() { lock(_gate) _items.Clear(); }
    }
}
