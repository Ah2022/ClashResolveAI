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
            public bool InputRefreshRequested;
            public IEnumerator<int>? InputRefresh;
            public long RefreshTriggerRevision=-1;
            public long VerifiedRevision=-1;
            public readonly HashSet<long> VerifiedSources=new HashSet<long>();
            public LiveScanStatus? Status;
        }
        private readonly Dictionary<string,State> _states=new Dictionary<string,State>();
        private readonly ChangeAccumulator _changes=new ChangeAccumulator();
        private readonly UIApplication _app;
        private readonly AlertSystem _alert;
        private ScanJob? _job;
        private IEnumerator<int>? _preparation;
        private readonly HashSet<long> _sources=new HashSet<long>();
        private AppSettings? _settings;
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
            bool returningFromOff=Mode==MonitorMode.Off&&mode!=MonitorMode.Off;
            foreach(var document in _states.Keys.ToList()){Abort(document,LiveScanOutcome.Cancelled,"Mode changed; unfinished changes retained");Get(document).TriggerWatermark=0;Get(document).Policy.ResumeRequested();Get(document).Failures=0;}
            Mode=mode;
            if(mode==MonitorMode.Off){foreach(var document in _states.Keys.ToList()){_changes.Clear(document);Get(document).InputRefresh?.Dispose();Get(document).InputRefresh=null;Get(document).InputRefreshRequested=false;}RadarDataStore.Instance.MarkStale(key,null,"Live checking disabled; re-check before using results");}
            var state=Get(key);
            SetStatus(key,LiveScanOutcome.Watching,
                mode==MonitorMode.Off?"Off":mode==MonitorMode.Trigger?"Waiting for Check Changes":"Watching");
            var doc=_app.ActiveUIDocument?.Document;
            if(returningFromOff&&doc!=null&&DocumentSession.Key(doc)==key)RefreshInputs(doc,key,"Monitoring resumed; rechecking the local session after Off");
        }
        public ScanStatistics? LastStatistics { get; private set; }
        public LiveScanStatus Status { get; private set; } = new LiveScanStatus(LiveScanOutcome.Watching,"Watching",0);
        private State Get(string key){if(!_states.TryGetValue(key,out var state))_states[key]=state=new State();return state;}
        public LiveScanScheduler(UIApplication app,AlertSystem alert){_app=app;_alert=alert;}
        public LiveScanStatus StatusFor(string key){var state=Get(key);return new LiveScanStatus(state.Status?.Outcome??LiveScanOutcome.Watching,state.Status?.Reason??(Mode==MonitorMode.Trigger?"Waiting for Check Changes":Mode==MonitorMode.Off?"Off":"Watching"),QueueDepth(key));}
        public int QueueDepth(string key)=>_changes.Count(key)+(_batch?.DocumentKey==key?_batch.Changes.Count:0);
        public bool NeedsPump(string key) {
            var state=Get(key);
            return !_disposed && Mode!=MonitorMode.Off && (DateTime.UtcNow>=state.NextPoll ||
                (state.Policy.CanRun&&state.Failures<3&&DateTime.UtcNow>=state.NextWork&&
                    (state.InputRefreshRequested||state.InputRefresh!=null||
                    (Dispatchable(key)&&(_batch!=null||_changes.Count(key)>0)))));
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
                // Resume an intact in-flight batch instead of scheduling the
                // same source twice when Radar rechecks its ledger after a pause.
                if(kind==LiveChangeKind.Check&&_batch?.DocumentKey==key&&_checked.Contains(id.Value)&&_revision==ScanCoordinator.DocumentRevision(key))continue;
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
            if(global)RefreshInputs(doc,key,"Model inputs changed; local Radar checks queued");
            else if(state.Policy.CanRun)SetStatus(key,LiveScanOutcome.Watching,Mode==MonitorMode.Trigger?"Waiting for Check Changes":"Changes queued");
        }
        private void RefreshInputs(Document doc,string key,string reason) {
            Abort(key,LiveScanOutcome.Superseded,reason);
            var state=Get(key);state.Environment="";state.NextPoll=DateTime.MinValue;
            state.Policy.ResumeRequested();state.Failures=0;state.TriggerWatermark=0;state.RefreshTriggerRevision=-1;state.VerifiedSources.Clear();
            state.InputRefresh?.Dispose();state.InputRefresh=null;state.InputRefreshRequested=true;
            RadarDataStore.Instance.MarkStale(key,null,reason);
            SetStatus(key,LiveScanOutcome.RecheckingInputs,Mode==MonitorMode.Trigger?"Inputs changed; click Check Changes":reason);
        }
        private IEnumerable<int> RequeueInputs(Document doc,string key) {
            // Recheck only monitored sources and the selection, against current
            // model candidates. Resolve one identity per iterator step, outside
            // DocumentChanged. Never populate this queue from Full Scan data.
            var ids=LiveSessionLedger.Store.Entries(key).Select(e=>e.ElementId).Concat(RadarDataStore.Instance.GetAll()
                .SelectMany(c=>new[]{c.EndpointA,c.EndpointB}).Where(e=>e.LinkInstanceUniqueId=="").Select(e=>e.ElementId));
            var selection=_app.ActiveUIDocument?.Document==doc?_app.ActiveUIDocument.Selection.GetElementIds().Select(id=>id.Value):Enumerable.Empty<long>();
            foreach(var id in ids.Concat(selection).Distinct()) {
                _changes.Record(key,id,doc.GetElement(new ElementId(id))?.UniqueId??"",LiveChangeKind.Check,DateTime.UtcNow);
                yield return 0;
            }
        }
        public void RequestCheck(Document doc,IEnumerable<ElementId> ids) {
            if(Mode==MonitorMode.Off)return;
            string key=DocumentSession.Key(doc);Get(key).Policy.ResumeRequested();Get(key).Failures=0;
            Record(doc,ids,LiveChangeKind.Check);
            Get(key).TriggerWatermark=_changes.Watermark(key);
            if(Get(key).InputRefreshRequested||Get(key).InputRefresh!=null)Get(key).RefreshTriggerRevision=ScanCoordinator.DocumentRevision(key);
        }
        public void Cancel(string key){Get(key).Policy.Cancelled=true;Abort(key,LiveScanOutcome.Cancelled,"Cancelled; unchecked changes retained");SetStatus(key,LiveScanOutcome.Cancelled,"Cancelled; unchecked changes retained");}
        public void Clear(string key){Abort(key,LiveScanOutcome.Cancelled,"Session cleared");_changes.Clear(key);var state=Get(key);state.InputRefresh?.Dispose();state.InputRefresh=null;state.InputRefreshRequested=false;state.RefreshTriggerRevision=-1;state.Policy.ResumeRequested();state.Failures=0;state.TriggerWatermark=0;SetStatus(key,LiveScanOutcome.Watching,"Session cleared");}
        public void SessionRenewed(string key,long generation){Abort(key,LiveScanOutcome.Superseded,"Session changed; unchecked changes retained");RadarDataStore.Instance.MarkStale(key,null,"Session changed; re-check before using results");}
        public void Close(string key){Abort(key,LiveScanOutcome.Cancelled,"Project closed");Get(key).InputRefresh?.Dispose();_changes.Close(key);_states.Remove(key);LiveDiagnostics.Close(key);}
        private void Abort(string key,LiveScanOutcome outcome,string reason) {
            if(_batch?.DocumentKey!=key)return;
            CaptureMetrics(key);
            _job?.Dispose();_job=null;_preparation?.Dispose();_preparation=null;_sources.Clear();_settings=null;_changes.Restore(_batch);
            LiveDiagnostics.Finish(key,outcome.ToString(),reason,QueueDepth(key)-_batch.Changes.Count,0,0,false,false);_batch=null;_checked.Clear();_staged.Clear();_captureIndex=0;SetStatus(key,outcome,reason);
        }
        private sealed class PreparationSuperseded : Exception { }
        private IEnumerable<int> PrepareSources(Document doc,string key) {
            var state=Get(key);
            var filter=ScanSessionCache.Filter(doc,_settings!);
            var tracked=new HashSet<long>(LiveSessionLedger.Store.Entries(key).Select(e=>e.ElementId));
            foreach(var change in _batch!.Changes) {
                var element=doc.GetElement(new ElementId(change.ElementId));
                if(element!=null&&!(element is ElementType)&&filter.PassesFilter(element)) {
                    if(change.UniqueId!=""&&element.UniqueId!=change.UniqueId){
                        _changes.Record(key,element.Id.Value,element.UniqueId,LiveChangeKind.Modified,DateTime.UtcNow);
                        throw new PreparationSuperseded();
                    }
                    _sources.Add(change.ElementId);
                    if(change.Kind==LiveChangeKind.Added)LiveSessionLedger.Note(doc,new[]{element.Id},LedgerOrigin.Drawn);
                    else if(change.Kind==LiveChangeKind.Modified&&(_settings!.TrackEditedElements||tracked.Contains(change.ElementId)))LiveSessionLedger.Note(doc,new[]{element.Id},LedgerOrigin.Edited);
                }
                yield return 0;
            }
            // A large adjacency set is additional local work, never a reason to
            // clear unchecked edits. Check overflow partners in subsequent batches.
            foreach(var row in RadarDataStore.Instance.GetAll()) {
                if(row.Status!=ClashStatus.Resolved&&row.Status!=ClashStatus.Ignored&&row.Status!=ClashStatus.Closed&&row.InvolvesHost(_affected))foreach(var endpoint in new[]{row.EndpointA,row.EndpointB}) {
                    if(endpoint.LinkInstanceUniqueId!=""||_sources.Contains(endpoint.ElementId)||state.VerifiedSources.Contains(endpoint.ElementId))continue;
                    var partner=doc.GetElement(new ElementId(endpoint.ElementId));
                    if(partner!=null&&partner.UniqueId==endpoint.UniqueId) {
                        if(_sources.Count<500)_sources.Add(endpoint.ElementId);
                        else _changes.Record(key,endpoint.ElementId,partner.UniqueId,LiveChangeKind.Check,DateTime.UtcNow);
                    }
                    yield return 0;
                }
                yield return 0;
            }
            _checked.UnionWith(_sources);
        }
        private T Measure<T>(string key,string stage,Func<T> work) {
            var timer=Stopwatch.StartNew();try{return work();}finally{LiveDiagnostics.Stage(key,stage,timer.Elapsed.TotalMilliseconds);}
        }
        private bool AdvancePreparation(string key,string stage,IEnumerator<int> iterator,Stopwatch slice,int target) {
            return Measure(key,stage,()=>{
                int count=0;
                do {if(!iterator.MoveNext())return true;}
                while(++count<128&&slice.Elapsed.TotalMilliseconds<target);
                return false;
            });
        }
        private void CaptureMetrics(string key){var stats=_job?.Statistics;if(stats!=null){LiveDiagnostics.Metrics(key,stats.Candidates,stats.Tested,stats.BooleanFailures,stats.MissingGeometry,stats.Unverified,stats.FirstResultUtc);LiveDiagnostics.EngineTimings(key,stats.CollectIndexMilliseconds,stats.CandidateMilliseconds,stats.GeometryMilliseconds,stats.BooleanMilliseconds,stats.SurfaceDistanceMilliseconds);}}
        private bool Protect(string key,double elapsed) {
            double maximum=Math.Max(50,Math.Min(1000,AppSettings.Load().LiveMaximumSliceMilliseconds));
            if(elapsed<=maximum)return false;
            var state=Get(key);
            if(_job!=null)LastStatistics=_job.Statistics;
            // Native Revit operations cannot be preempted. Keep the iterator and
            // captured DTOs, then yield so the next callback continues that work.
            state.NextWork=DateTime.UtcNow.AddMilliseconds(Math.Min(1000,Math.Max(100,elapsed)));
            if(elapsed>=Math.Max(1000,maximum*10)) {
                state.Policy.ProtectionPaused=true;
                SetStatus(key,LiveScanOutcome.ProtectionPaused,"Paused: slow Revit operation; Check Changes continues retained progress");
                Diagnostics.Log($"Live API callback exceeded hard protection limit: {elapsed:F1} ms; progress retained");
            } else {
                if(_job!=null)SetStatus(key,LiveScanOutcome.Checking,"Slow Revit operation; continuing retained progress");
                Diagnostics.Log($"Live API callback exceeded preferred limit: {elapsed:F1} ms; yielding with progress retained");
            }
            return true;
        }
        public void Advance(string key,long generation) {
            var doc=_app.ActiveUIDocument?.Document;
            if(_disposed||Mode==MonitorMode.Off||doc==null||!doc.IsValidObject||DocumentSession.Key(doc)!=key||!LiveMonitorService.Instance.IsCurrent(key,generation))return;
            var state=Get(key);
            var apiSlice=Stopwatch.StartNew();
            try {
                int target=Math.Max(5,Math.Min(40,AppSettings.Load().LiveSliceMilliseconds));
                // Input validation can cost more than the preferred scan slice
                // on large hosts. Poll once per second, so the next callback
                // can advance geometry rather than repeatedly exhausting its
                // budget on the same input fingerprint. In-flight work skips
                // background polling; publication below
                // still validates fresh inputs before exposing any results.
                if(state.Environment==""||(_batch==null&&DateTime.UtcNow>=state.NextPoll)) {
                    string current=Measure(key,"Environment",()=>LiveEnvironment.Capture(doc));
                    state.NextPoll=DateTime.UtcNow.AddSeconds(1);
                    if(state.Environment!=""&&state.Environment!=current)RefreshInputs(doc,key,"Rules or loaded links changed; local Radar checks queued");
                    state.Environment=current;
                }
                string environment=state.Environment;
                if(!state.Policy.CanRun||state.Failures>=3||DateTime.UtcNow<state.NextWork||doc.IsReadOnly)return;
                if(state.InputRefreshRequested||state.InputRefresh!=null) {
                    if(state.InputRefresh==null){state.InputRefresh=RequeueInputs(doc,key).GetEnumerator();state.InputRefreshRequested=false;}
                    bool refreshed=AdvancePreparation(key,"InputRefresh",state.InputRefresh,apiSlice,target);
                    if(refreshed){
                        state.InputRefresh.Dispose();state.InputRefresh=null;
                        if(Mode==MonitorMode.Trigger&&state.RefreshTriggerRevision==ScanCoordinator.DocumentRevision(key))state.TriggerWatermark=_changes.Watermark(key);
                        state.RefreshTriggerRevision=-1;
                    }
                    SetStatus(key,LiveScanOutcome.RecheckingInputs,Mode==MonitorMode.Trigger?"Inputs changed; click Check Changes":"Preparing local input rechecks");
                    if(Protect(key,apiSlice.Elapsed.TotalMilliseconds)||!refreshed)return;
                }
                if(!Dispatchable(key)||!state.Policy.CanRun||state.Failures>=3||DateTime.UtcNow<state.NextWork||doc.IsReadOnly)return;
                if(_batch!=null&&(_batch.SessionGeneration!=generation||_revision!=ScanCoordinator.DocumentRevision(key)||_stamp!=environment))
                    Abort(key,LiveScanOutcome.Superseded,"Scan inputs changed; batch restored");
                if(_batch==null) {
                    if(Mode==MonitorMode.Live&&!_changes.Ready(key,DateTime.UtcNow,Math.Max(300,Math.Min(700,AppSettings.Load().LiveDebounceMilliseconds))))return;
                    if(_changes.Count(key)==0)return;
                    _staged.Clear();_captureIndex=0;
                    _batch=_changes.Detach(key,generation,DateTime.UtcNow,500,Mode==MonitorMode.Trigger?state.TriggerWatermark:long.MaxValue);LiveDiagnostics.Batch(_batch,Mode,ScanCoordinator.DocumentRevision(key),environment,QueueDepth(key));_checked=new HashSet<long>(_batch.Changes.Select(c=>c.ElementId));_affected=new HashSet<long>(_checked);
                    _revision=ScanCoordinator.DocumentRevision(key);_stamp=environment;
                    if(state.VerifiedRevision!=_revision){state.VerifiedSources.Clear();state.VerifiedRevision=_revision;}
                    _settings=AppSettings.Load().ScanSnapshot();_sources.Clear();
                    _preparation=PrepareSources(doc,key).GetEnumerator();
                }
                if(_preparation!=null) {
                    bool prepared=AdvancePreparation(key,"Preparation",_preparation,apiSlice,target);
                    if(!prepared){SetStatus(key,LiveScanOutcome.Checking,$"Preparing {_checked.Count} elements");Protect(key,apiSlice.Elapsed.TotalMilliseconds);return;}
                    _preparation.Dispose();_preparation=null;
                    _job=Measure(key,"Preparation",()=>new ClashResolveAI.ClashEngine.ClashEngine(doc,_settings!.RuleSetName)
                        .CreateJob(_sources.Select(id=>new ElementId(id)),_settings.ScanLinkedModels,settingsSnapshot:_settings));
                }
                if(Protect(key,apiSlice.Elapsed.TotalMilliseconds))return;
                if(apiSlice.Elapsed.TotalMilliseconds>=target&&!_job!.Complete){SetStatus(key,LiveScanOutcome.Checking,$"Preparing {_checked.Count} elements");return;}
                LiveDiagnostics.Started(key);
                bool done=_job!.Advance(Math.Max(1,target-(int)apiSlice.Elapsed.TotalMilliseconds));
                CaptureMetrics(key);
                if(Protect(key,apiSlice.Elapsed.TotalMilliseconds))return;
                state.NextWork=DateTime.UtcNow.AddMilliseconds(25);
                if(!done){if(DateTime.UtcNow>=_nextStatus){SetStatus(key,LiveScanOutcome.Checking,$"Checking {_checked.Count} elements · {_job.Statistics.Tested} pairs tested");_nextStatus=DateTime.UtcNow.AddMilliseconds(250);}return;}
                if(!LiveMonitorService.Instance.IsCurrent(key,generation)||_revision!=ScanCoordinator.DocumentRevision(key)){Abort(key,LiveScanOutcome.Superseded,"Completion superseded; batch restored");return;}
                LastStatistics=_job.Statistics;
                // DTO capture is API work too. Publish only after all rows have
                // been captured over budgeted callbacks and revalidated.
                while(_captureIndex<_job.Results.Count&&apiSlice.Elapsed.TotalMilliseconds<target){
                    var row=_job.Results[_captureIndex++];
                    row.GeometryRevision=_revision;row.SessionGeneration=generation;row.Origin=ResultOrigin.Live;row.LiveSessionId=RadarDataStore.Instance.SessionId;
                    if(row.InvolvesHost(_affected))_staged.Add(Measure(key,"DtoCapture",()=>LiveClashResolver.Capture(doc,row,_stamp)));
                    if(Protect(key,apiSlice.Elapsed.TotalMilliseconds))return;
                }
                if(_captureIndex<_job.Results.Count){SetStatus(key,LiveScanOutcome.Checking,"Preparing verified results");return;}
                // Validate only after budgeted DTO capture is complete. Doing
                // this before capture on every callback can starve capture too.
                if(_stamp!=Measure(key,"Environment",()=>LiveEnvironment.Capture(doc))){RefreshInputs(doc,key,"Inputs changed before publication; local Radar check queued");return;}
                // Fresh validation already completed. Publish these detached DTOs
                // even if validation was slow; the finally block yields afterward.
                var results=_staged.ToList();
                var coverage=_job.Statistics.Scope;var mode=_job.Statistics.Mode;
                bool Covered(LiveClashDto row) {
                    // A missing UniqueId is proof of deletion only in its host;
                    // unloaded links are incomplete coverage, not deletions.
                    if((row.LinkInstanceA==""&&_checked.Contains(row.ElementAId)&&doc.GetElement(row.EndpointA.UniqueId)==null)||
                       (row.LinkInstanceB==""&&_checked.Contains(row.ElementBId)&&doc.GetElement(row.EndpointB.UniqueId)==null))return true;
                    return coverage.ContainsPair(key,row.ElementAId,row.LinkInstanceA,row.ElementBId,row.LinkInstanceB,row.LegacyPairKey);
                }
                var outcome=Measure(key,"Reconciliation",()=>RadarDataStore.Instance.Reconcile(_affected,results,mode,Covered));
                var reliable=new HashSet<long>(_checked.Where(id=>coverage.IsHostReliable(id)||doc.GetElement(new ElementId(id))==null));
                state.VerifiedSources.UnionWith(reliable);
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
                _job.Dispose();_job=null;_batch=null;_checked.Clear();_sources.Clear();_settings=null;_staged.Clear();_captureIndex=0;state.Failures=0;
                LiveDiagnostics.Finish(key,incomplete?"Unverified":"Completed",incomplete?"Some pairs unverified":"Local check completed",QueueDepth(key),outcome.NewClashes,outcome.ResolvedClashes,true,results.Count>0);
                SetStatus(key,incomplete?LiveScanOutcome.Unverified:LiveScanOutcome.Completed,$"{outcome.NewClashes} new clashes · {outcome.ResolvedClashes} clash(es) resolved"+(incomplete?" · Some pairs unverified":""));
                if(Mode!=MonitorMode.Off&&outcome.NewRows.Count>0&&AppSettings.Load().ShowToast)_alert.TriggerLiveAlerts(outcome.NewRows);
                if(ClashRadarPanel.IsVisible)ClashRadarPanel.Instance.UpdateLedgerCount();
            }catch(PreparationSuperseded){Abort(key,LiveScanOutcome.Superseded,"Element identity changed; local check queued");}
            catch(Exception ex){state.Failures++;state.NextWork=DateTime.UtcNow.AddSeconds(5);Abort(key,LiveScanOutcome.Failed,"Live check failed; pending changes retained");Diagnostics.Log("Live scheduler failed",ex);SetStatus(key,LiveScanOutcome.Failed,"Live check failed; pending changes retained");}
            finally{
                double elapsed=apiSlice.Elapsed.TotalMilliseconds;
                MaximumApiSliceMilliseconds=Math.Max(MaximumApiSliceMilliseconds,elapsed);
                LiveDiagnostics.Slice(key,elapsed,QueueDepth(key));
                // A single native operation cannot be interrupted. If final
                // reconciliation overran, its verified publication stands. Yield
                // subsequent callbacks without replaying or discarding that batch.
                if(elapsed>Math.Max(50,Math.Min(1000,AppSettings.Load().LiveMaximumSliceMilliseconds))&&state.Policy.CanRun&&DateTime.UtcNow>=state.NextWork)
                    Protect(key,elapsed);
            }
        }
        public void Dispose(){if(_disposed)return;_disposed=true;_job?.Dispose();_preparation?.Dispose();foreach(var state in _states.Values)state.InputRefresh?.Dispose();_job=null;_preparation=null;_batch=null;_states.Clear();_changes.ClearAll();}
    }
}
