using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Alert;
using ClashResolveAI.ClashEngine;
using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ClashResolveAI.LiveMonitor
{
    // Sole owner of the live ScanJob. Invoked only by gateway API callbacks;
    // pending changes and status contain plain values and survive interrupted work.
    internal sealed class LiveScanScheduler : IDisposable
    {
        private sealed class State {
            public readonly LiveSchedulingState Policy=new LiveSchedulingState();
            public string Environment="";
            public DateTime NextPoll,NextWork;
            public int Failures;
            public long TriggerWatermark;
            public LiveScanStatus? Status;
        }
        private readonly Dictionary<string,State> _states=new Dictionary<string,State>();
        private readonly ChangeAccumulator _changes=new ChangeAccumulator();
        private readonly UIApplication _app;
        private readonly AlertSystem _alert;
        private ScanJob? _job;
        private LiveChangeBatch? _batch;
        private HashSet<long> _checked=new HashSet<long>();
        private HashSet<long> _affected=new HashSet<long>();
        private string _stamp="";
        private long _revision;
        private DateTime _nextStatus;
        private readonly List<LiveClashDto> _staged=new List<LiveClashDto>();
        private int _captureIndex;
        public double MaximumApiSliceMilliseconds {get;private set;}
        private bool _disposed;
        public MonitorMode Mode {get;private set;}=MonitorMode.Live;
        private bool Dispatchable(string key)=>Mode==MonitorMode.Live||(Mode==MonitorMode.Trigger&&Get(key).TriggerWatermark>0&&(_batch?.DocumentKey==key||_changes.Pending(key).Any(c=>c.Sequence<=Get(key).TriggerWatermark)));
        public void SetMode(string key,MonitorMode mode){
            if(Mode==mode)return;
            foreach(var document in _states.Keys.ToList()){Abort(document,LiveScanOutcome.Cancelled,"Mode changed; unfinished changes retained");Get(document).TriggerWatermark=0;Get(document).Policy.ResumeRequested();Get(document).Failures=0;}
            Mode=mode;
            if(mode==MonitorMode.Off){foreach(var document in _states.Keys.ToList())_changes.Clear(document);RadarDataStore.Instance.MarkStale(key,null,"Live checking disabled; re-check before using results");}
            var state=Get(key);
            SetStatus(key,state.Policy.FullScanReason!=""&&mode!=MonitorMode.Off?LiveScanOutcome.RequiresFullScan:LiveScanOutcome.Watching,
                mode==MonitorMode.Off?"Off":state.Policy.FullScanReason!=""?state.Policy.FullScanReason:mode==MonitorMode.Trigger?"Waiting for Check Changes":"Watching");
        }
        public ScanStatistics? LastStatistics { get; private set; }
        public LiveScanStatus Status { get; private set; } = new LiveScanStatus(LiveScanOutcome.Watching,"Watching",0);
        private State Get(string key){if(!_states.TryGetValue(key,out var state))_states[key]=state=new State();return state;}
        public LiveScanScheduler(UIApplication app,AlertSystem alert){_app=app;_alert=alert;}
        public LiveScanStatus StatusFor(string key){var state=Get(key);return new LiveScanStatus(state.Status?.Outcome??LiveScanOutcome.Watching,state.Status?.Reason??(Mode==MonitorMode.Trigger?"Waiting for Check Changes":Mode==MonitorMode.Off?"Off":"Watching"),QueueDepth(key));}
        public int QueueDepth(string key)=>_changes.Count(key)+(_batch?.DocumentKey==key?_batch.Changes.Count:0);
        public bool NeedsPump(string key) {
            var state=Get(key);
            return !_disposed && Mode!=MonitorMode.Off && !state.Policy.FullScanRunning && (DateTime.UtcNow>=state.NextPoll ||
                (Dispatchable(key)&&state.Policy.CanRun&&state.Failures<3&&DateTime.UtcNow>=state.NextWork&&(_job!=null||_changes.Count(key)>0)));
        }
        private void SetStatus(string key,LiveScanOutcome outcome,string reason) {
            Status=new LiveScanStatus(outcome,reason,QueueDepth(key));Get(key).Status=Status;
            LiveMonitorService.Instance.PublishStatus(key,Status);
            LiveDiagnostics.Status(key,Mode,Status);
        }
        public void Record(Document doc,IEnumerable<ElementId> ids,LiveChangeKind kind,bool global=false,DateTime? documentChangedUtc=null) {
            if(Mode==MonitorMode.Off)return;
            string key=DocumentSession.Key(doc);var state=Get(key);var changed=new HashSet<long>();var now=documentChangedUtc??DateTime.UtcNow;
            foreach(var id in ids) {
                string uid=kind==LiveChangeKind.Deleted?"":doc.GetElement(id)?.UniqueId??"";
                _changes.Record(key,id.Value,uid,kind,now);changed.Add(id.Value);
            }
            if(changed.Count==0&&!global)return;
            if(kind!=LiveChangeKind.Check) {
                LiveDiagnostics.Changed(key,LiveMonitorService.Instance.SessionGeneration(key),Mode,now,changed.Count,QueueDepth(key));
                state.Failures=0;state.Policy.Cancelled=false;
                RadarDataStore.Instance.MarkStale(key,global?null:changed,"Model changed; pending local check");
                Abort(key,LiveScanOutcome.Superseded,"New edits queued; interrupted batch preserved");
            }
            if(global)RequireFullScan(key,"Model inputs changed; Full Scan required");
            else if(state.Policy.CanRun)SetStatus(key,LiveScanOutcome.Watching,Mode==MonitorMode.Trigger?"Waiting for Check Changes":"Changes queued");
        }
        public void RequireFullScan(string key,string reason) {
            if(Get(key).Policy.FullScanReason==reason)return;
            Get(key).Policy.RequireFullScan(reason);Abort(key,LiveScanOutcome.RequiresFullScan,reason);
            RadarDataStore.Instance.MarkStale(key,null,reason);SetStatus(key,LiveScanOutcome.RequiresFullScan,reason);
        }
        public void RequestCheck(Document doc,IEnumerable<ElementId> ids) {
            if(Mode==MonitorMode.Off)return;
            string key=DocumentSession.Key(doc);Get(key).Policy.ResumeRequested();Get(key).Failures=0;
            Record(doc,ids,LiveChangeKind.Check);
            Get(key).TriggerWatermark=_changes.Watermark(key);
            if(Get(key).Policy.FullScanReason!="")SetStatus(key,LiveScanOutcome.RequiresFullScan,Get(key).Policy.FullScanReason);
        }
        public void Cancel(string key){Get(key).Policy.Cancelled=true;Abort(key,LiveScanOutcome.Cancelled,"Cancelled; unchecked changes retained");SetStatus(key,LiveScanOutcome.Cancelled,"Cancelled; unchecked changes retained");}
        public void Clear(string key){Abort(key,LiveScanOutcome.Cancelled,"Session cleared");_changes.Clear(key);var state=Get(key);state.Policy.ResumeRequested();state.Failures=0;SetStatus(key,state.Policy.FullScanReason!=""?LiveScanOutcome.RequiresFullScan:LiveScanOutcome.Watching,state.Policy.FullScanReason!=""?state.Policy.FullScanReason:"Session cleared");}
        public void SessionRenewed(string key,long generation){Abort(key,LiveScanOutcome.Superseded,"Session changed; unchecked changes retained");RadarDataStore.Instance.MarkStale(key,null,"Session changed; re-check before using results");}
        public void Close(string key){Abort(key,LiveScanOutcome.Cancelled,"Project closed");_changes.Close(key);_states.Remove(key);LiveDiagnostics.Close(key);}
        private void Abort(string key,LiveScanOutcome outcome,string reason) {
            if(_batch?.DocumentKey!=key)return;
            CaptureMetrics(key);
            _job?.Dispose();_job=null;_changes.Restore(_batch);
            LiveDiagnostics.Finish(key,outcome.ToString(),reason,QueueDepth(key)-_batch.Changes.Count,0,0,false,false);_batch=null;_checked.Clear();_staged.Clear();_captureIndex=0;SetStatus(key,outcome,reason);
        }
        public void FullScanStarted(string key,long revision) {
            Abort(key,LiveScanOutcome.PausedForFullScan,"Full Scan running; changes retained");
            Get(key).Policy.BeginFullScan(_changes.Watermark(key),revision);
            SetStatus(key,LiveScanOutcome.PausedForFullScan,"Full Scan running; changes retained");
        }
        public void FullScanEnded(Document? doc,string key,bool success,bool fullScope,ScanScope? scope,long revision) {
            var state=Get(key);
            if(doc!=null&&DocumentSession.Key(doc)!=key)doc=null;
            success=success&&doc!=null&&ScanCoordinator.DocumentRevision(key)==revision;
            bool resume=state.Policy.EndFullScan(success&&doc!=null,fullScope,revision);
            if(success&&doc!=null&&scope!=null&&revision==state.Policy.FullScanRevision)_changes.AcknowledgeThrough(key,state.Policy.FullScanWatermark,id=>scope.IsHostReliable(id)||(fullScope&&doc?.GetElement(new ElementId(id))==null));
            if(resume&&doc!=null) {
                state.Environment=LiveEnvironment.Capture(doc);
                LiveMonitorService.Instance.RenewAfterFullScan(key);
                // Full Scan publishes only Dashboard. Revalidate our monitored
                // sources to produce fresh generation-stamped live DTOs.
                var ids=LiveSessionLedger.LiveIds(doc).Concat(RadarDataStore.Instance.GetAll()
                    .SelectMany(c=>new[]{c.EndpointA,c.EndpointB}).Where(e=>e.LinkInstanceUniqueId=="").Select(e=>new ElementId(e.ElementId))).Distinct(new IdComparer()).ToList();
                Record(doc,ids,LiveChangeKind.Check);
            }
            SetStatus(key,state.Policy.FullScanReason!=""?LiveScanOutcome.RequiresFullScan:LiveScanOutcome.Watching,
                state.Policy.FullScanReason!=""?state.Policy.FullScanReason:success?"Full Scan complete; local changes retained":"Full Scan stopped; local changes retained");
        }
        private sealed class IdComparer : IEqualityComparer<ElementId> {public bool Equals(ElementId? a,ElementId? b)=>a?.Value==b?.Value;public int GetHashCode(ElementId id)=>id.Value.GetHashCode();}
        private void CaptureMetrics(string key){var stats=_job?.Statistics;if(stats!=null)LiveDiagnostics.Metrics(key,stats.Candidates,stats.Tested,stats.BooleanFailures,stats.MissingGeometry,stats.Unverified,stats.FirstResultUtc);}
        private bool Protect(string key,double elapsed) {
            double maximum=Math.Max(50,Math.Min(1000,AppSettings.Load().LiveMaximumSliceMilliseconds));
            if(elapsed<=maximum)return false;
            if(_job!=null)LastStatistics=_job.Statistics;
            Get(key).Policy.ProtectionPaused=true;
            Abort(key,LiveScanOutcome.ProtectionPaused,"Paused: Revit responsiveness protection");
            SetStatus(key,LiveScanOutcome.ProtectionPaused,"Paused: Revit responsiveness protection");
            Diagnostics.Log($"Live API callback exceeded protection limit: {elapsed:F1} ms; pending batch preserved");
            return true;
        }
        public void Advance(string key,long generation) {
            var doc=_app.ActiveUIDocument?.Document;
            if(_disposed||Mode==MonitorMode.Off||doc==null||!doc.IsValidObject||DocumentSession.Key(doc)!=key||!LiveMonitorService.Instance.IsCurrent(key,generation))return;
            var state=Get(key);
            var apiSlice=Stopwatch.StartNew();
            try {
                string environment=LiveEnvironment.Capture(doc);state.NextPoll=DateTime.UtcNow.AddSeconds(1);
                if(state.Environment!=""&&state.Environment!=environment)RequireFullScan(key,"Rules or loaded-link inputs changed; Full Scan required");
                state.Environment=environment;
                if(ScanCoordinator.InputsStale)RequireFullScan(key,"Model inputs changed; Full Scan required");
                if(!Dispatchable(key)||!state.Policy.CanRun||state.Failures>=3||DateTime.UtcNow<state.NextWork||doc.IsReadOnly)return;
                if(_job!=null&&(_batch!.SessionGeneration!=generation||_revision!=ScanCoordinator.DocumentRevision(key)||_stamp!=environment))
                    Abort(key,LiveScanOutcome.Superseded,"Scan inputs changed; batch restored");
                if(_job==null) {
                    if(Mode==MonitorMode.Live&&!_changes.Ready(key,DateTime.UtcNow,Math.Max(300,Math.Min(700,AppSettings.Load().LiveDebounceMilliseconds))))return;
                    _staged.Clear();_captureIndex=0;
                    _batch=_changes.Detach(key,generation,DateTime.UtcNow,500,Mode==MonitorMode.Trigger?state.TriggerWatermark:long.MaxValue);LiveDiagnostics.Batch(_batch,Mode,ScanCoordinator.DocumentRevision(key),environment,QueueDepth(key));_checked=new HashSet<long>(_batch.Changes.Select(c=>c.ElementId));_affected=new HashSet<long>(_checked);
                    var filter=ScanSessionCache.Filter(doc,AppSettings.Load());var sources=new HashSet<long>();
                    foreach(var change in _batch.Changes) {
                        var element=doc.GetElement(new ElementId(change.ElementId));
                        if(element==null||element is ElementType||!filter.PassesFilter(element))continue;
                        if(change.UniqueId!=""&&element.UniqueId!=change.UniqueId){RequireFullScan(key,"Element identities changed before checking; Full Scan required");return;}
                        sources.Add(change.ElementId);
                        if(change.Kind==LiveChangeKind.Added)LiveSessionLedger.Note(doc,new[]{element.Id},LedgerOrigin.Drawn);
                        else if(change.Kind==LiveChangeKind.Modified&&(AppSettings.Load().TrackEditedElements||LiveSessionLedger.Store.Entries(key).Any(e=>e.ElementId==change.ElementId)))LiveSessionLedger.Note(doc,new[]{element.Id},LedgerOrigin.Edited);
                    }
                    // Include former host partners: moving a target can invalidate
                    // a monitored source even when rule direction differs.
                    foreach(var row in RadarDataStore.Instance.GetAll().Where(c=>c.InvolvesHost(_checked)))
                        foreach(var endpoint in new[]{row.EndpointA,row.EndpointB})if(endpoint.LinkInstanceUniqueId==""&&doc.GetElement(endpoint.UniqueId)!=null)sources.Add(endpoint.ElementId);
                    if(sources.Count>2000){RequireFullScan(key,"Dependency scope exceeds the local safety limit; Full Scan required");return;}
                    _checked.UnionWith(sources);LiveSessionLedger.LiveIds(doc);
                    _revision=ScanCoordinator.DocumentRevision(key);_stamp=environment;
                    _job=new ClashResolveAI.ClashEngine.ClashEngine(doc,AppSettings.Load().RuleSetName)
                        .CreateJob(sources.Select(id=>new ElementId(id)),AppSettings.Load().ScanLinkedModels,settingsSnapshot:AppSettings.Load().ScanSnapshot());
                }
                if(Protect(key,apiSlice.Elapsed.TotalMilliseconds))return;
                int target=Math.Max(5,Math.Min(40,AppSettings.Load().LiveSliceMilliseconds));
                if(apiSlice.Elapsed.TotalMilliseconds>=target&&!_job.Complete){SetStatus(key,LiveScanOutcome.Checking,$"Preparing {_checked.Count} elements");return;}
                LiveDiagnostics.Started(key);
                bool done=_job.Advance(Math.Max(1,target-(int)apiSlice.Elapsed.TotalMilliseconds));
                CaptureMetrics(key);
                if(Protect(key,apiSlice.Elapsed.TotalMilliseconds))return;
                state.NextWork=DateTime.UtcNow.AddMilliseconds(25);
                if(!done){if(DateTime.UtcNow>=_nextStatus){SetStatus(key,LiveScanOutcome.Checking,$"Checking {_checked.Count} elements · {_job.Statistics.Tested} pairs tested");_nextStatus=DateTime.UtcNow.AddMilliseconds(250);}return;}
                if(!LiveMonitorService.Instance.IsCurrent(key,generation)||_revision!=ScanCoordinator.DocumentRevision(key)||_stamp!=LiveEnvironment.Capture(doc)){Abort(key,LiveScanOutcome.Superseded,"Completion superseded; batch restored");return;}
                LastStatistics=_job.Statistics;
                // DTO capture is API work too. Publish only after all rows have
                // been captured over budgeted callbacks and revalidated.
                while(_captureIndex<_job.Results.Count&&apiSlice.Elapsed.TotalMilliseconds<target){
                    var row=_job.Results[_captureIndex++];
                    row.GeometryRevision=_revision;row.SessionGeneration=generation;row.Origin=ResultOrigin.Live;row.LiveSessionId=RadarDataStore.Instance.SessionId;
                    if(row.InvolvesHost(_affected))_staged.Add(LiveClashResolver.Capture(doc,row,_stamp));
                    if(Protect(key,apiSlice.Elapsed.TotalMilliseconds))return;
                }
                if(_captureIndex<_job.Results.Count){SetStatus(key,LiveScanOutcome.Checking,"Preparing verified results");return;}
                var results=_staged.ToList();
                var coverage=_job.Statistics.Scope;var mode=_job.Statistics.Mode;
                bool Covered(LiveClashDto row) {
                    // A missing UniqueId is proof of deletion only in its host;
                    // unloaded links are incomplete coverage, not deletions.
                    if((row.LinkInstanceA==""&&_checked.Contains(row.ElementAId)&&doc.GetElement(row.EndpointA.UniqueId)==null)||
                       (row.LinkInstanceB==""&&_checked.Contains(row.ElementBId)&&doc.GetElement(row.EndpointB.UniqueId)==null))return true;
                    return coverage.ContainsPair(key,row.ElementAId,row.LinkInstanceA,row.ElementBId,row.LinkInstanceB,row.LegacyPairKey);
                }
                var outcome=RadarDataStore.Instance.Reconcile(_affected,results,mode,Covered);
                var reliable=new HashSet<long>(_checked.Where(id=>coverage.IsHostReliable(id)||doc.GetElement(new ElementId(id))==null));
                ScanCoordinator.MarkChecked(key,reliable);
                Diagnostics.Log($"Live batch {_batch!.BatchId}: document {key}; generation {generation}; watermark {_batch.Watermark}; {_job.Statistics.Summary}; new {outcome.NewClashes}; resolved {outcome.ResolvedClashes}");
                var sourceFilter=ScanSessionCache.Filter(doc,AppSettings.Load());
                bool NeedsReview(long id){var element=doc.GetElement(new ElementId(id));return element!=null&&!(element is ElementType)&&sourceFilter.PassesFilter(element)&&!coverage.IsHostReliable(id);}
                bool incomplete=LastStatistics.MissingGeometry>0||LastStatistics.BooleanFailures>0||LastStatistics.Unverified>0||_batch.Changes.Any(c=>NeedsReview(c.ElementId));
                if(incomplete){
                    _changes.Restore(_batch);
                    _changes.AcknowledgeThrough(key,_batch.Watermark,id=>!NeedsReview(id));
                    state.Policy.Cancelled=true; // Await an edit/explicit recheck; no blind retries.
                }
                _job.Dispose();_job=null;_batch=null;_checked.Clear();_staged.Clear();_captureIndex=0;state.Failures=0;
                LiveDiagnostics.Finish(key,incomplete?"Unverified":"Completed",incomplete?"Some pairs unverified":"Local check completed",QueueDepth(key),outcome.NewClashes,outcome.ResolvedClashes,true,results.Count>0);
                SetStatus(key,incomplete?LiveScanOutcome.Unverified:LiveScanOutcome.Completed,$"{outcome.NewClashes} new clashes · {outcome.ResolvedClashes} clash(es) resolved"+(incomplete?" · Some pairs unverified":""));
                if(Mode!=MonitorMode.Off&&outcome.NewRows.Count>0&&AppSettings.Load().ShowToast)_alert.TriggerLiveAlerts(outcome.NewRows);
                if(ClashRadarPanel.IsVisible)ClashRadarPanel.Instance.UpdateLedgerCount();
            }catch(Exception ex){state.Failures++;state.NextWork=DateTime.UtcNow.AddSeconds(5);Abort(key,LiveScanOutcome.Failed,"Live check failed; pending changes retained");Diagnostics.Log("Live scheduler failed",ex);SetStatus(key,LiveScanOutcome.Failed,"Live check failed; pending changes retained");}
            finally{
                double elapsed=apiSlice.Elapsed.TotalMilliseconds;
                MaximumApiSliceMilliseconds=Math.Max(MaximumApiSliceMilliseconds,elapsed);
                LiveDiagnostics.Slice(key,elapsed,QueueDepth(key));
                // A single native operation cannot be interrupted. If final
                // reconciliation overran, its verified publication stands and
                // further callbacks are paused rather than replaying that batch.
                if(elapsed>Math.Max(50,Math.Min(1000,AppSettings.Load().LiveMaximumSliceMilliseconds))&&state.Policy.CanRun)
                    Protect(key,elapsed);
            }
        }
        public void Dispose(){if(_disposed)return;_disposed=true;_job?.Dispose();_job=null;_batch=null;_states.Clear();_changes.ClearAll();}
    }
}
