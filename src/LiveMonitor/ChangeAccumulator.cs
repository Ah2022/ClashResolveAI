using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ClashResolveAI.LiveMonitor
{
    public enum LiveChangeKind { Added, Modified, Deleted, Check }
    public sealed class LiveElementChange
    {
        public long ElementId { get; }
        public string UniqueId { get; }
        public LiveChangeKind Kind { get; }
        public long Sequence { get; }
        public DateTime ChangedUtc { get; }
        public LiveElementChange(long id,string uniqueId,LiveChangeKind kind,long sequence,DateTime changed)
        {ElementId=id;UniqueId=uniqueId;Kind=kind;Sequence=sequence;ChangedUtc=changed;}
    }
    public sealed class LiveChangeBatch
    {
        public Guid BatchId { get; } = Guid.NewGuid();
        public string DocumentKey { get; }
        public long SessionGeneration { get; }
        public long Watermark { get; }
        public DateTime CreatedUtc { get; }
        public IReadOnlyList<LiveElementChange> Changes { get; }
        internal LiveChangeBatch(string key,long generation,long watermark,IEnumerable<LiveElementChange> changes,DateTime now)
        {DocumentKey=key;SessionGeneration=generation;Watermark=watermark;Changes=new ReadOnlyCollection<LiveElementChange>(changes.ToList());CreatedUtc=now;}
    }
    // Plain state. Detaching a batch moves ownership; later edits of the same ID
    // remain pending and are never removed by completing the older batch.
    public sealed class ChangeAccumulator
    {
        private sealed class State { public long Sequence;public DateTime? FirstPendingUtc;public readonly Dictionary<long,LiveElementChange> Pending=new Dictionary<long,LiveElementChange>(); }
        private readonly Dictionary<string,State> _states=new Dictionary<string,State>();
        private State Get(string key) {if(!_states.TryGetValue(key,out var state))_states[key]=state=new State();return state;}
        public long Watermark(string key)=>Get(key).Sequence;
        public int Count(string key)=>Get(key).Pending.Count;
        public IReadOnlyList<LiveElementChange> Pending(string key)=>Get(key).Pending.Values.ToList().AsReadOnly();
        public void Record(string key,long id,string uniqueId,LiveChangeKind kind,DateTime now) {
            var state=Get(key);
            // Selection/recheck requests must not erase the origin, deletion,
            // identity or revision of a committed change waiting in the queue.
            if(kind==LiveChangeKind.Check&&state.Pending.ContainsKey(id))return;
            if(state.Pending.Count==0)state.FirstPendingUtc=now;
            state.Pending[id]=new LiveElementChange(id,uniqueId,kind,++state.Sequence,now);
        }
        public bool Ready(string key,DateTime now,int debounceMilliseconds,int maximumAgeMilliseconds=2000) {
            var values=Get(key).Pending.Values;
            return values.Count>0 && ((now-values.Max(c=>c.ChangedUtc)).TotalMilliseconds>=debounceMilliseconds ||
                (now-Get(key).FirstPendingUtc!.Value).TotalMilliseconds>=maximumAgeMilliseconds);
        }
        public LiveChangeBatch Detach(string key,long generation,DateTime now,int maximumElements=int.MaxValue,long maximumSequence=long.MaxValue) {
            var state=Get(key);var selected=state.Pending.Values.Where(c=>c.Sequence<=maximumSequence).OrderBy(c=>c.Sequence).Take(maximumElements).ToList();
            var batch=new LiveChangeBatch(key,generation,Math.Min(state.Sequence,maximumSequence),selected,now);
            foreach(var change in selected)state.Pending.Remove(change.ElementId);
            state.FirstPendingUtc=state.Pending.Count==0?(DateTime?)null:state.Pending.Values.Min(c=>c.ChangedUtc);return batch;
        }
        public void Restore(LiveChangeBatch batch) {
            var state=Get(batch.DocumentKey);
            foreach(var change in batch.Changes)
                if(!state.Pending.TryGetValue(change.ElementId,out var later)||later.Sequence<change.Sequence)state.Pending[change.ElementId]=change;
            if(state.Pending.Count>0)state.FirstPendingUtc=state.Pending.Values.Min(c=>c.ChangedUtc);
        }
        public void AcknowledgeThrough(string key,long watermark,Func<long,bool> covered) {
            var state=Get(key);
            foreach(var change in state.Pending.Values.ToList())
                if(change.Sequence<=watermark && covered(change.ElementId))state.Pending.Remove(change.ElementId);
        }
        public void Clear(string key) {Get(key).Pending.Clear();Get(key).FirstPendingUtc=null;} // Keep the watermark monotonic.
        public void Close(string key)=>_states.Remove(key);
        public void ClearAll()=>_states.Clear();
    }

    public enum LiveScanOutcome { Watching, Checking, Completed, Cancelled, Superseded, Unverified, RequiresFullScan, PausedForFullScan, ProtectionPaused, Failed }
    public sealed class LiveScanStatus
    {
        public LiveScanOutcome Outcome { get; }
        public string Reason { get; }
        public int QueueDepth { get; }
        public LiveScanStatus(LiveScanOutcome outcome,string reason,int queueDepth){Outcome=outcome;Reason=reason;QueueDepth=queueDepth;}
        public bool RequiresFullScan=>Outcome==LiveScanOutcome.RequiresFullScan;
    }
    // Session-level pauses are independent: Clear/Cancel cannot certify inputs.
    internal sealed class LiveSchedulingState
    {
        public bool FullScanRunning,Cancelled,ProtectionPaused;
        public string FullScanReason="";
        public long FullScanWatermark,FullScanRevision;
        public bool CanRun=>!FullScanRunning&&!Cancelled&&!ProtectionPaused&&FullScanReason=="";
        public void RequireFullScan(string reason){FullScanReason=reason;}
        public void ResumeRequested(){Cancelled=false;ProtectionPaused=false;}
        public void BeginFullScan(long watermark,long revision){FullScanRunning=true;FullScanWatermark=watermark;FullScanRevision=revision;}
        public bool EndFullScan(bool success,bool fullScope,long currentRevision) {
            FullScanRunning=false;
            if(!success||!fullScope||currentRevision!=FullScanRevision)return false;
            FullScanReason="";ProtectionPaused=false;return true;
        }
    }
}
