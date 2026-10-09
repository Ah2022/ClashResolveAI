using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ClashResolveAI.LiveMonitor
{
    // Immutable, exportable snapshots: no model names, settings secrets or Revit objects.
    public sealed class LiveDiagnosticSnapshot
    {
        public DateTime TimestampUtc {get;}
        public string Event {get;}
        public string DocumentKey {get;}
        public long SessionGeneration {get;}
        public Guid? RequestId {get;}
        public long? RequestGeneration {get;}
        public Guid? BatchId {get;}
        public long GeometryRevision {get;}
        public MonitorMode Mode {get;}
        public string InputFingerprint {get;}
        public DateTime? DocumentChangedUtc {get;}
        public DateTime? BatchCreatedUtc {get;}
        public DateTime? ScanStartedUtc {get;}
        public DateTime? FirstResultUtc {get;}
        public DateTime? FirstPublishedResultUtc {get;}
        public DateTime? ScanCompletedUtc {get;}
        public DateTime? TerminalUtc {get;}
        public double ScanElapsedMilliseconds {get;}
        public int QueueDepth {get;}
        public int RequestQueueDepth {get;}
        public int ChangedElementCount {get;}
        public int CandidateCount {get;}
        public int TestedPairCount {get;}
        public int BooleanFailures {get;}
        public int MissingGeometry {get;}
        public int UnverifiedCount {get;}
        public double MaximumApiSliceMilliseconds {get;}
        // Live scheduler totals are cumulative for the open session. Engine
        // timings describe the current/last batch, copied from its existing metrics.
        public double EnvironmentValidationMilliseconds {get;}
        public double InputRefreshMilliseconds {get;}
        public double PreparationMilliseconds {get;}
        public double DtoCaptureMilliseconds {get;}
        public double ReconciliationMilliseconds {get;}
        public double MaximumStageMilliseconds {get;}
        public string SlowestStage {get;}
        public double EngineCollectionMilliseconds {get;}
        public double CandidateMilliseconds {get;}
        public double GeometryMilliseconds {get;}
        public double BooleanMilliseconds {get;}
        public double SurfaceDistanceMilliseconds {get;}
        public int ResolvedClashCount {get;}
        public int NewClashCount {get;}
        public long StaleRequestCount {get;}
        public long CoalescedRequestCount {get;}
        public long WakeRetryCount {get;}
        public string Outcome {get;}
        public string Reason {get;}
        internal LiveDiagnosticSnapshot(LiveDiagnosticState s,string eventName)
        {
            TimestampUtc=DateTime.UtcNow;Event=eventName;DocumentKey=s.Document;SessionGeneration=s.Generation;
            RequestId=s.Request;RequestGeneration=s.RequestGeneration;BatchId=s.Batch;GeometryRevision=s.Revision;Mode=s.Mode;InputFingerprint=s.Fingerprint;
            DocumentChangedUtc=s.Changed;BatchCreatedUtc=s.Created;ScanStartedUtc=s.Started;FirstResultUtc=s.First;FirstPublishedResultUtc=s.Published;ScanCompletedUtc=s.Completed;TerminalUtc=s.Terminal;
            ScanElapsedMilliseconds=s.Clock?.Elapsed.TotalMilliseconds??0;QueueDepth=s.Queue;RequestQueueDepth=s.RequestQueue;ChangedElementCount=s.ChangedCount;
            CandidateCount=s.Candidates;TestedPairCount=s.Tested;BooleanFailures=s.Booleans;MissingGeometry=s.Missing;UnverifiedCount=s.Unverified;
            MaximumApiSliceMilliseconds=s.MaximumSlice;ResolvedClashCount=s.Resolved;NewClashCount=s.New;
            EnvironmentValidationMilliseconds=s.EnvironmentMs;InputRefreshMilliseconds=s.RefreshMs;PreparationMilliseconds=s.PreparationMs;
            DtoCaptureMilliseconds=s.DtoMs;ReconciliationMilliseconds=s.ReconciliationMs;MaximumStageMilliseconds=s.MaximumStageMs;SlowestStage=s.SlowestStage;
            EngineCollectionMilliseconds=s.CollectionMs;CandidateMilliseconds=s.CandidateMs;GeometryMilliseconds=s.GeometryMs;BooleanMilliseconds=s.BooleanMs;SurfaceDistanceMilliseconds=s.SurfaceMs;
            StaleRequestCount=s.Stale;CoalescedRequestCount=s.Coalesced;WakeRetryCount=s.Retries;Outcome=s.Outcome;Reason=s.Reason;
        }
    }
    internal sealed class LiveDiagnosticState
    {
        public string Document="",Fingerprint="",Outcome="Watching",Reason="";
        public long Generation,Revision,Stale,Coalesced,Retries;
        public Guid? Request,Batch;
        public long? RequestGeneration;
        public MonitorMode Mode;
        public DateTime? Changed,Created,Started,First,Published,Completed,Terminal;
        public DateTime NextSlice;
        public Stopwatch? Clock;
        public int Queue,RequestQueue,ChangedCount,Candidates,Tested,Booleans,Missing,Unverified,Resolved,New;
        public double MaximumSlice;
        public double EnvironmentMs,RefreshMs,PreparationMs,DtoMs,ReconciliationMs,MaximumStageMs;
        public double CollectionMs,CandidateMs,GeometryMs,BooleanMs,SurfaceMs;
        public string SlowestStage="";
    }
    public static class LiveDiagnostics
    {
        private static readonly object Gate=new object();
        private static readonly Dictionary<string,LiveDiagnosticState> States=new Dictionary<string,LiveDiagnosticState>();
        private static readonly Queue<LiveDiagnosticSnapshot> History=new Queue<LiveDiagnosticSnapshot>();
        public const int HistoryCapacity=512;
        public static event Action<LiveDiagnosticSnapshot>? Recorded;
        private static LiveDiagnosticState Get(string key){if(!States.TryGetValue(key,out var s))States[key]=s=new LiveDiagnosticState {Document=key};return s;}
        private static void Write(string key,string eventName,Action<LiveDiagnosticState> update,bool emit=true)
        {
            LiveDiagnosticSnapshot? snapshot=null;
            lock(Gate){var s=Get(key);update(s);if(emit){snapshot=new LiveDiagnosticSnapshot(s,eventName);while(History.Count>=HistoryCapacity)History.Dequeue();History.Enqueue(snapshot);}}
            if(snapshot!=null&&Recorded!=null)foreach(Action<LiveDiagnosticSnapshot> sink in Recorded.GetInvocationList())try{sink(snapshot);}catch{/* Diagnostics cannot stop Revit work. */}
        }
        public static IReadOnlyList<LiveDiagnosticSnapshot> Export(string key){lock(Gate)return History.Where(s=>s.DocumentKey==key).ToList().AsReadOnly();}
        public static LiveDiagnosticSnapshot Current(string key){lock(Gate)return new LiveDiagnosticSnapshot(Get(key),"Snapshot");}
        internal static void Session(string key,long generation,MonitorMode mode)=>Write(key,"Session",s=>{s.Generation=generation;s.Mode=mode;});
        internal static void Changed(string key,long generation,MonitorMode mode,DateTime timestamp,int count,int queue)=>Write(key,"DocumentChanged",s=>{s.Generation=generation;s.Mode=mode;s.Changed=timestamp;s.ChangedCount=count;s.Queue=queue;});
        internal static void Batch(LiveChangeBatch batch,MonitorMode mode,long revision,string fingerprint,int queue)=>Write(batch.DocumentKey,"BatchCreated",s=>{
            s.Generation=batch.SessionGeneration;s.Mode=mode;s.Batch=batch.BatchId;s.Revision=revision;s.Fingerprint=fingerprint;s.Created=batch.CreatedUtc;
            var committed=batch.Changes.Where(c=>c.Kind!=LiveChangeKind.Check).ToList();s.Changed=committed.Count==0?(DateTime?)null:committed.Min(c=>c.ChangedUtc);
            s.Started=s.First=s.Published=s.Completed=s.Terminal=null;s.Clock=null;s.Candidates=s.Tested=s.Booleans=s.Missing=s.Unverified=s.Resolved=s.New=0;s.MaximumSlice=0;s.ChangedCount=batch.Changes.Count;s.Queue=queue;s.Outcome="Preparing";s.Reason="";
            s.CollectionMs=s.CandidateMs=s.GeometryMs=s.BooleanMs=s.SurfaceMs=0;
        });
        internal static void Started(string key){lock(Gate)if(Get(key).Started!=null)return;Write(key,"ScanStarted",s=>{s.Started=DateTime.UtcNow;s.Clock=Stopwatch.StartNew();});}
        internal static void Stage(string key,string stage,double milliseconds)=>Write(key,"Stage",s=>{
            switch(stage){case "Environment":s.EnvironmentMs+=milliseconds;break;case "InputRefresh":s.RefreshMs+=milliseconds;break;case "Preparation":s.PreparationMs+=milliseconds;break;case "DtoCapture":s.DtoMs+=milliseconds;break;case "Reconciliation":s.ReconciliationMs+=milliseconds;break;}
            if(milliseconds>s.MaximumStageMs){s.MaximumStageMs=milliseconds;s.SlowestStage=stage;}
        },false);
        internal static void EngineTimings(string key,double collection,double candidates,double geometry,double booleans,double surface)=>Write(key,"EngineTimings",s=>{
            s.CollectionMs=collection;s.CandidateMs=candidates;s.GeometryMs=geometry;s.BooleanMs=booleans;s.SurfaceMs=surface;
        },false);
        internal static void Metrics(string key,int candidates,int tested,int booleans,int missing,int unverified,DateTime? first)=>Write(key,"Metrics",s=>{s.Candidates=candidates;s.Tested=tested;s.Booleans=booleans;s.Missing=missing;s.Unverified=unverified;s.First=s.First??first;},false);
        internal static void Finish(string key,string outcome,string reason,int queue,int added,int resolved,bool completed,bool hasResults)=>Write(key,"BatchFinished",s=>{s.Clock?.Stop();s.Terminal=DateTime.UtcNow;if(completed)s.Completed=s.Terminal;if(hasResults)s.Published=s.Terminal;s.Outcome=outcome;s.Reason=reason;s.Queue=queue;s.New=added;s.Resolved=resolved;});
        internal static void Status(string key,MonitorMode mode,LiveScanStatus status)=>Write(key,"Status",s=>{s.Mode=mode;s.Queue=status.QueueDepth;s.Outcome=status.Outcome.ToString();s.Reason=status.Reason;});
        internal static void Slice(string key,double milliseconds,int queue)
        {
            bool emit;lock(Gate){var s=Get(key);emit=DateTime.UtcNow>=s.NextSlice||milliseconds>s.MaximumSlice;if(emit)s.NextSlice=DateTime.UtcNow.AddMilliseconds(250);}
            Write(key,"ApiSlice",s=>{s.MaximumSlice=Math.Max(s.MaximumSlice,milliseconds);s.Queue=queue;},emit);
        }
        internal static void Request(string key,long generation,Guid request,string operation,int queue,long coalesced,bool stale=false)=>Write(key,stale?"RequestRejected":"Request"+operation,s=>{s.Request=request;s.RequestGeneration=generation;s.RequestQueue=queue;s.Coalesced=coalesced;if(stale)s.Stale++;},stale||operation!="Advance");
        internal static void Retry(string key)=>Write(key,"WakeRetry",s=>s.Retries++);
        internal static void Close(string key){lock(Gate)States.Remove(key);}
    }
}
