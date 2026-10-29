using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ClashResolveAI.ClashEngine;
using ClashResolveAI.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Dashboard.Persistence;
using Newtonsoft.Json;

namespace ClashResolveAI.Core
{
    internal static class ScanCoordinator
    {
        private static ScanJob? _job;
        private static string _documentKey="";
        private static Action<List<ClashResult>,ScanStatistics>? _done;
        private static Action<string>? _progress;
        private static Action<string>? _failed;
        private sealed class Pump : IExternalEventHandler
        {
            public void Execute(UIApplication app) => Advance(app);
            public string GetName() => "ClashResolve full scan pipeline";
        }
        private static ExternalEvent? _event;
        private static System.Windows.Threading.DispatcherTimer? _timer;
        private static DateTime _nextProgress;
        private static readonly ElementRevisionStore Revisions=new ElementRevisionStore();
        private static readonly Dictionary<string,HashSet<long>> Inputs=new Dictionary<string,HashSet<long>>();
        private static bool _completeScope;
        private static long _startRevision;
        private static string _startEnvironment="";
        private static ScanPlan? _activePlan;
        private static string CaptureEnvironment(Document doc)
        {
            if(_activePlan==null)return LiveMonitor.LiveEnvironment.CaptureForFullScan(doc);
            var s=AppSettings.Load();var ruleName=_activePlan.RuleSetName;
            var model=RevitScanCapture.Capture(doc,s.FullScanSnapshot(),true,"",null,_activePlan.Mode,_activePlan.LinkToLink).ModelFingerprint;
            return ProjectClassificationStore.Hash(model+"|"+JsonConvert.SerializeObject(new {s.FullScanMode,s.FullScanRuleSetName,s.FullScanLinkedModels,s.FullScanWithinLinks,
                s.ScanHostToHost,s.ScanHostToLinks,s.IncludeLinkToLink,s.FullScanIncludeGenericModels,s.FullScanIncludeInsulation,s.FullScanExcludeNamedSupports,
                s.FullScanIncludeStructural,s.FullScanExcludeConnectedJoints,s.FullScanMinimumOverlapMM3,s.FullScanInsulationMM,s.FullScanMaintenanceMM,
                Rules=Rules.RulesEngine.CacheKey(ruleName),Keywords=SystemKeywordConfig.Load()}));
        }
        private static readonly HashSet<string> MappingDirty=new HashSet<string>();
        private static DateTime _nextMappingCheck;
        public static void MappingsChanged(string documentKey)
        {
            MappingDirty.Add(documentKey);
            if(Busy&&_documentKey==documentKey)Cancel("Project classification mappings changed. Run the scan again.");
            if(documentKey==DocumentSession.CurrentKey)Dashboard.ClashDashboard.Instance.NotifyGeometryChanged();
        }
        public static string BusyDocumentKey=>_documentKey;
        public static long StartRevision=>_startRevision;
        public static void Observe(Document doc)
        {
            string key=DocumentSession.Key(doc);if(Inputs.ContainsKey(key))return;
            Inputs[key]=new HashSet<long>(new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds().Select(id=>id.Value)
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Level)).ToElementIds().Select(id=>id.Value))
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Family)).ToElementIds().Select(id=>id.Value))
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).ToElementIds().Select(id=>id.Value)));
        }
        public static long DocumentRevision(string key)=>Revisions.DocumentRevision(key);
        public static long InputRevision(string key)=>Revisions.InputRevision(key);
        public static bool IsStale(LiveMonitor.LiveClashDto row)=>Revisions.Required(row.HostDocumentKey,row.ElementAId,row.LinkInstanceA,row.ElementBId,row.LinkInstanceB)>row.GeometryRevision;
        public static bool InputsStale => Revisions.InputsStale(DocumentSession.CurrentKey);
        public static bool FullResultsStale => MappingDirty.Contains(DocumentSession.CurrentKey)||Revisions.FullResultsStale(DocumentSession.CurrentKey);
        public static bool IsInput(string key,long id)=>Inputs.TryGetValue(key,out var ids)&&ids.Contains(id);
        public static bool IsStale(ClashResult clash)=>Revisions.IsStale(clash);
        public static long RequiredRevision(ClashResult clash)=>Revisions.Required(clash);
        public static void MarkChecked(string key,ISet<long> ids){Revisions.Checked(key,ids);LiveMonitor.LiveSessionLedger.Store.Checked(key,ids);}
        public static void Close(string key){Revisions.Close(key);Inputs.Remove(key);MappingDirty.Remove(key);LiveMonitor.LiveSessionLedger.Store.Clear(key);}
        public static bool Busy => _job!=null;
        public static ScanStatistics? CurrentStatistics => _job?.Statistics;
        public static long Revision=>Revisions.Revision;
        public static bool ResultsStale=>DocumentSession.CurrentKey!=""&&(Revisions.IsDirty(DocumentSession.CurrentKey)||LiveMonitor.LiveSessionLedger.Store.HasUnchecked(DocumentSession.CurrentKey));
        public static void Attach(UIControlledApplication app)
        {
            _event=ExternalEvent.Create(new Pump());
            _timer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(10) };
            _timer.Tick+=(_,__)=>{if(_job!=null)_event?.Raise();};
            app.ControlledApplication.DocumentChanged+=(_,e)=>{
                var doc=e.GetDocument();if(doc.IsFamilyDocument)return;
                if(ModelChangePolicy.IsViewOnly(e.GetTransactionNames()))return;
                string key=DocumentSession.Key(doc);
                if(doc.IsLinked){Revisions.Changed(key,e.GetAddedElementIds().Concat(e.GetModifiedElementIds()).Concat(e.GetDeletedElementIds()).Select(id=>id.Value),true);return;}
                Observe(doc);
                var all=e.GetAddedElementIds().Concat(e.GetModifiedElementIds()).Concat(e.GetDeletedElementIds()).ToList();
                bool inputs=all.Any(id=>Inputs[key].Contains(id.Value)||doc.GetElement(id) is ElementType||doc.GetElement(id) is Family||doc.GetElement(id) is Level||doc.GetElement(id) is RevitLinkInstance||doc.GetElement(id) is ProjectLocation||doc.GetElement(id) is BasePoint);
                foreach(var id in all)if(doc.GetElement(id) is ElementType||doc.GetElement(id) is Family||doc.GetElement(id) is Level||doc.GetElement(id) is RevitLinkInstance||doc.GetElement(id) is ProjectLocation||doc.GetElement(id) is BasePoint)Inputs[key].Add(id.Value);
                ScanSessionCache.Changed(doc,all);
                var scope=ScanSessionCache.Filter(doc,AppSettings.Load());
                var ids=e.GetAddedElementIds(scope).Concat(e.GetModifiedElementIds(scope)).Where(id=>!(doc.GetElement(id) is ElementType)).Concat(e.GetDeletedElementIds()).Select(id=>id.Value).ToList();
                if(all.Count==0&&!inputs)return;
                // Advance document revision for every model transaction, but
                // only supported geometry IDs belong in the dirty-result set.
                Revisions.Changed(key,ids,inputs);
                if(_job!=null&&key==_documentKey)Cancel("Model changed during scan. Run the scan again.");
                if(key==DocumentSession.CurrentKey)Dashboard.ClashDashboard.Instance.NotifyGeometryChanged();
                if(LiveMonitor.ClashRadarPanel.IsVisible)LiveMonitor.ClashRadarPanel.Instance.NotifyGeometryChanged();
            };
            app.ControlledApplication.DocumentClosing+=(_,e)=>{if(_job!=null&&DocumentSession.Key(e.Document)==_documentKey)Cancel("Project closed.");ScanSessionCache.Close(e.Document);};
        }
        public static void Start(Document doc, string level,CoordinationZone? zone,Action<List<ClashResult>,ScanStatistics> done,Action<string>? progress=null,Action<string>? failed=null,ScanMode? mode=null,bool? includeLinkToLink=null)
        {
            var settings=AppSettings.Load().FullScanSnapshot();
            var job=new ClashResolveAI.ClashEngine.ClashEngine(doc,settings.RuleSetName).CreateJob(null,settings.FullScanLinkedModels,zone,level,mode,includeLinkToLink,settingsSnapshot:settings);
            StartJob(doc,job,done,progress,failed,job.Statistics.CoversWholeHostScope);
        }
        public static void Start(Document doc,ScanPlan plan,Action<List<ClashResult>,ScanStatistics> done,Action<string>? progress=null,Action<string>? failed=null)
        {
            if(ScanStaging.IsStaged(plan)) {
                var workflow=Guid.NewGuid().ToString("N");var hard=ScanStaging.HardPass(plan);
                var first=new ClashResolveAI.ClashEngine.ClashEngine(doc,hard.RuleSetName).CreatePlannedJob(hard);
                first.Statistics.CoversWholeHostScope=false;first.Statistics.WorkflowId=workflow;first.Statistics.WorkflowStageCount=2;first.Statistics.StageLabel="Hard clashes — saved before clearance";
                StartJob(doc,first,(rows,stats)=>{
                    try {
                        var clearance=ScanStaging.ClearancePass(plan,rows);
                        if(clearance==null){stats.StageLabel="Hard clashes saved; clearance skipped — no confirmed areas";progress?.Invoke(stats.CoordinatorSummary);done(rows,stats);return;}
                        var second=new ClashResolveAI.ClashEngine.ClashEngine(doc,clearance.RuleSetName).CreatePlannedJob(clearance);
                        second.Statistics.WorkflowId=workflow;second.Statistics.WorkflowStage=2;second.Statistics.WorkflowStageCount=2;
                        second.Statistics.StageLabel="Clearance — "+clearance.CoordinationAreas.Count+" confirmed areas";
                        StartJob(doc,second,done,progress,failed,false);
                    } catch(Exception ex){failed?.Invoke("Hard-clash stage saved. Clearance could not start: "+ex.Message);}
                },progress,failed,false);return;
            }
            var job=new ClashResolveAI.ClashEngine.ClashEngine(doc,plan.RuleSetName).CreatePlannedJob(plan);
            job.Statistics.StageLabel=plan.Stage==ScanStage.LinkToLink?"Explicit link-to-link job":plan.Stage==ScanStage.InsulationClearance?"Dedicated insulation clearance":plan.Mode==ScanMode.HardOnly?"Hard clashes":"Hard and selected clearance";
            StartJob(doc,job,done,progress,failed,job.Statistics.CoversWholeHostScope);
        }
        internal static void StartJob(Document doc,ScanJob job,Action<List<ClashResult>,ScanStatistics> done,Action<string>? progress=null,Action<string>? failed=null,bool completeScope=false)
        {
            Cancel("Replaced by a newer scan.");
            try {
                DocumentSession.Activate(doc);
                bool customCapture=string.IsNullOrEmpty(job.Statistics.ScanCaptureJson);
                var capture=customCapture
                    ?RevitScanCapture.Capture(doc,AppSettings.Load().ScanSnapshot(),AppSettings.Load().ScanLinkedModels,"",null,job.Statistics.Mode,AppSettings.Load().IncludeLinkToLink)
                    :JsonConvert.DeserializeObject<ScanCapture>(job.Statistics.ScanCaptureJson)!;
                if(customCapture){capture.FullModelScope=completeScope;capture.ScopeJson="{\"CaptureSource\":\"CustomJob\",\"Scope\":\"Unspecified\"}";}
                var attempt=ClashDatabase.Instance.History.BeginScan(capture);
                job.Statistics.ScanId=attempt.ScanId;job.Statistics.ScanSequenceNumber=attempt.SequenceNumber;
                _documentKey=DocumentSession.Key(doc);_job=job;
                _activePlan=string.IsNullOrEmpty(job.Statistics.ScanPlanJson)?null:JsonConvert.DeserializeObject<ScanPlan>(job.Statistics.ScanPlanJson);
                _startRevision=DocumentRevision(_documentKey);_startEnvironment=CaptureEnvironment(doc);
                _completeScope=completeScope;
                _done=done;_progress=progress;_failed=failed;_nextProgress=DateTime.MinValue;_nextMappingCheck=DateTime.MinValue;
                _timer?.Start();_event?.Raise();
            } catch(Exception ex) {
                _timer?.Stop();_job=null;_done=null;_progress=null;_failed=null;
                if(job.Statistics.ScanId!="") {
                    try {ClashDatabase.Instance.History.EndAttempt(job.Statistics.ScanId,ScanVersionState.Failed,ex.Message);}
                    catch(Exception historyError){Diagnostics.Log("Could not persist scan startup failure",historyError);}
                }
                job.Dispose();throw;
            }
        }
        public static void Cancel(string reason="Scan cancelled; previous results retained.")
        {
            _timer?.Stop();
            var job=_job;var failed=_failed;_job=null;_done=null;_progress=null;_failed=null;
            if(job!=null){
                try {ClashDatabase.Instance.History.EndAttempt(job.Statistics.ScanId,ScanVersionState.Cancelled,reason);}
                catch(Exception ex){Diagnostics.Log("Could not persist scan cancellation",ex);reason+=" History write failed: "+ex.Message;}
                job.Dispose();failed?.Invoke(reason);
            }
        }
        public static void Shutdown()
        { Cancel("Revit closing.");Commands.DashboardCommand.ShutdownApiActions();_timer?.Stop();_timer=null;_event?.Dispose();_event=null;ScanSessionCache.Clear(); }
        private static void Advance(UIApplication app)
        {
            if(_job==null)return;
            if(app.ActiveUIDocument==null||DocumentSession.Key(app.ActiveUIDocument.Document)!=_documentKey){Cancel("Active project changed.");return;}
            try {
                var job=_job;
                if(DateTime.UtcNow>=_nextMappingCheck) {
                    _nextMappingCheck=DateTime.UtcNow.AddSeconds(1);
                    if(job.Statistics.ProjectMappingsFingerprint!=""&&new ProjectClassificationStore().Load(_documentKey).Fingerprint!=job.Statistics.ProjectMappingsFingerprint)
                    {MappingDirty.Add(_documentKey);Cancel("Project classification mappings changed. Run the scan again.");return;}
                }
                if(DocumentRevision(_documentKey)!=_startRevision||CaptureEnvironment(app.ActiveUIDocument.Document)!=_startEnvironment){Cancel("Full Scan inputs changed; pending live changes retained.");return;}
                bool done=job.Advance(Math.Max(10,Math.Min(50,AppSettings.Load().FullScanSliceMilliseconds)));
                if(done||DateTime.UtcNow>=_nextProgress)
                { _progress?.Invoke(job.Statistics.CoordinatorSummary); _nextProgress=DateTime.UtcNow.AddMilliseconds(250); }
                if(!ReferenceEquals(_job,job))return; // Progress/cancel UI can dispose the job synchronously.
                if(!done)return;
                _timer?.Stop();
                var callback=_done;var failure=_failed;_job=null;_done=null;_progress=null;_failed=null;
                foreach(var c in job.Results)c.GeometryRevision=_startRevision;
                bool successful=false;
                string completedKey=_documentKey;
                long completedRevision=_startRevision;
                bool completeScope=_completeScope;
                try {
                    var currentDoc=app.ActiveUIDocument.Document;
                    if(job.Statistics.ProjectMappingsFingerprint!=""&&new ProjectClassificationStore().Load(completedKey).Fingerprint!=job.Statistics.ProjectMappingsFingerprint)
                    {MappingDirty.Add(completedKey);throw new InvalidOperationException("Project classification mappings changed before scan publication.");}
                    if(DocumentRevision(completedKey)!=completedRevision||CaptureEnvironment(currentDoc)!=_startEnvironment)
                        throw new InvalidOperationException("Full Scan inputs changed; pending live changes retained. Run Full Scan again.");
                    Dashboard.ClashDashboard.Instance.CompleteFullScan(job.Results,job.Statistics);
                    if(DocumentRevision(completedKey)!=completedRevision)throw new InvalidOperationException("Model changed during Full Scan publication.");
                    var doc=app.ActiveUIDocument.Document;
                    successful=job.Statistics.MissingGeometry==0&&job.Statistics.BooleanFailures==0&&job.Statistics.Unverified==0;
                    Revisions.CheckedScope(completedKey,id=>job.Statistics.Scope.IsHostReliable(id)||(completeScope&&doc.GetElement(new ElementId(id))==null),completeScope&&successful);
                    if(completeScope&&successful)MappingDirty.Remove(completedKey);
                    if(LiveMonitor.ClashRadarPanel.IsVisible)LiveMonitor.ClashRadarPanel.Instance.NotifyGeometryChanged();
                    try {callback?.Invoke(job.Results,job.Statistics);}
                    catch(Exception presentationError){
                        Diagnostics.Log("Saved scan dashboard presentation failed",presentationError);
                        TaskDialog.Show("Full Scan saved",$"V{job.Statistics.ScanSequenceNumber:D3} was saved successfully. The dashboard could not open: {presentationError.Message}");
                    }
                } catch(Exception ex){
                    try {ClashDatabase.Instance.History.EndAttempt(job.Statistics.ScanId,ScanVersionState.Failed,ex.Message);}
                    catch(Exception historyError){Diagnostics.Log("Could not persist scan failure",historyError);}
                    failure?.Invoke(ex.Message);throw;
                } finally { job.Dispose(); }
                Diagnostics.Log("Scan complete: "+job.Statistics.Summary);
            }catch(Exception ex){
                Diagnostics.Log("Scan job failed",ex);
                if(_job!=null) {
                    try {ClashDatabase.Instance.History.EndAttempt(_job.Statistics.ScanId,ScanVersionState.Failed,ex.Message);}
                    catch(Exception historyError){Diagnostics.Log("Could not persist scan failure",historyError);}
                }
                Cancel(ex.Message);
            }
        }
    }
}
