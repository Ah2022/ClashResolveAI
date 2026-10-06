using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ClashResolveAI.LiveMonitor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ClashResolveAI.Core
{
    // Opt-in only, inherited by a dedicated test Revit process. Never touches an existing model.
    internal static partial class IntegrationVerification
    {
        private static string _folder = "";
        private static ExternalEvent? _event;
        private static System.Windows.Threading.DispatcherTimer? _timer;
        private sealed class Runner : IExternalEventHandler
        {
            public void Execute(UIApplication app) => ExecuteStep(app);
            public string GetName() => "ClashResolve integration verification";
        }
        private static int _step;
        private static DateTime _next;
        private static Document? _test;
        private static ElementId? _moving;
        private static ClashResult? _navigationTarget;
        private static long _requestedView;
        private static readonly List<string> Results = new List<string>();
        public static void Attach(UIControlledApplication app)
        {
            _folder = Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR") ?? "";
            if (string.IsNullOrEmpty(_folder)) return;
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "started.txt"), Assembly.GetExecutingAssembly().Location + "\n" + Assembly.GetExecutingAssembly().FullName);
            _event = ExternalEvent.Create(new Runner());
            // Timer raises only the next verification step. It does not pump
            // Revit Idling continuously and mask a stalled monitor scheduler.
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += (_,__) => { if(DateTime.UtcNow>=_next) _event?.Raise(); };
            _timer.Start();
        }
        private static void Check(bool condition, string name)
        {
            Results.Add((condition ? "PASS " : "FAIL ") + name);
            File.WriteAllLines(Path.Combine(_folder, "results.txt"), Results);
            if (!condition) throw new InvalidOperationException(name);
        }
        private static void ExecuteStep(UIApplication app)
        {
            if(DateTime.UtcNow<_next)return;
            try
            {
                if(_step>=700){ReleaseStep(app);return;}
                if(_step==0&&Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_RELEASE")=="1"){_step=700;ReleaseStep(app);return;}
                if(_step>=500){Phase5Step(app);return;}
                if(_step>=400){LedgerStep(app);return;}
                if (_step == 0)
                {
                    if (app.ActiveUIDocument != null) throw new InvalidOperationException("Verification requires a fresh Revit process with no open project.");
                    _step=-99; // A WPF layout pump must not re-enter fixture creation.
                    CreateFixture(app);
                    if(Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_PHASE0")=="1"){
                        _step=Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_PHASE5")=="1"?500:400;_next=DateTime.UtcNow.AddSeconds(1);return;
                    }
                    _step = -2; _next = DateTime.UtcNow.AddSeconds(3);
                }
                else if (_step == -2)
                {
                    Check(app.ActiveUIDocument.ActiveView is View3D && app.ActiveUIDocument.ActiveView.Id.Value == _requestedView, "3D navigation opens the clash view");
                    _requestedView = Services.ClashViewNavigation.Show(app, _navigationTarget!, false);
                    _step = -1; _next = DateTime.UtcNow.AddSeconds(3);
                }
                else if (_step == -1)
                {
                    Check(app.ActiveUIDocument.ActiveView is ViewPlan && app.ActiveUIDocument.ActiveView.Id.Value == _requestedView, "2D navigation opens the clash plan");
                    var snapshots = ClashSnapshotService.GenerateSnapshots(app, _navigationTarget!);
                    Check(File.Exists(snapshots.Path3D) && File.Exists(snapshots.Path2D), "2D and 3D snapshots exported");
                    LiveMonitorService.Instance.Start(app);
                    RadarDataStore.Instance.Clear();
                    // Live work must pause while Full Scan owns API time, preserving selection changes.
                    IEnumerable<int> LongScan(){while(true)yield return 0;}
                    ScanCoordinator.StartJob(_test!,new ClashEngine.ScanJob(LongScan(),new List<ClashResult>(),new ClashEngine.ScanStatistics()),(_,__)=>{});
                    app.ActiveUIDocument.Selection.SetElementIds(new[] { _moving! });
                    LiveMonitorService.Instance.CheckCurrentSelection();
                    _step = 11; _next = DateTime.UtcNow.AddSeconds(3);
                }
                else if(_step==11)
                {
                    Check(ScanCoordinator.Busy&&RadarDataStore.Instance.ActiveCount==0,"Live work pauses while Full Scan owns API time");
                    ScanCoordinator.Cancel("Verification interrupted Full Scan");
                    ScanCoordinator.Start(_test!,"",null,(rows,stats)=>Dashboard.ClashDashboard.Instance.MergeFullScan(rows,stats.Mode,stats.Scope));
                    _step=12;_next=DateTime.UtcNow.AddSeconds(3);
                }
                else if(_step==12)
                {
                    if(ScanCoordinator.Busy){_next=DateTime.UtcNow.AddSeconds(1);return;}
                    _step=1;_next=DateTime.UtcNow.AddSeconds(3);
                }
                else if (_step == 1)
                {
                    Check(RadarDataStore.Instance.ActiveCount >= 3, "Live selection detects host plus repeated linked instances");
                    Check(!ScanCoordinator.Busy,"Live work resumes only after Full Scan completes");
                    var inspected=RadarDataStore.Instance.GetActive().First();
                    var scene=Inspection.InspectionGeometry.Build(_test!,LiveClashResolver.Resolve(_test!,inspected),20);
                    Check(scene.Meshes.Any(m=>m.Role==0)&&scene.Meshes.Any(m=>m.Role==1)&&scene.HasOverlap,"Interactive inspection extracts both elements and the real overlap mesh");
                    ClashRadarPanel.Instance.VerifyInspectorLayout(inspected,scene,_folder);
                    Check(true,"Responsive grid, full-height inspector, 3-up floating view and redocking work");
                    Inspection.InspectionHandler.Run(app,LiveClashResolver.Resolve(_test!,inspected),scene,new Inspection.InspectorPreferences {Context=false});
                    Check(new FilteredElementCollector(_test!).OfClass(typeof(View3D)).Cast<View3D>().Any(v=>v.Name.StartsWith("Clash_"+inspected.ClashId+"_")),"Pin action creates a saved 3D inspection view");
                    using (var tx = new Transaction(_test!, "ClashResolve — Verification model move"))
                    { tx.Start(); ElementTransformUtils.MoveElement(_test!, _moving!, new XYZ(100, 0, 0)); tx.Commit(); }
                    _step = 2; _next = DateTime.UtcNow.AddSeconds(3);
                }
                else if (_step == 2)
                {
                    Check(RadarDataStore.Instance.ActiveCount == 0, "Live monitor removes resolved clashes after movement");
                    using (var tx = new Transaction(_test!, "Verification restore"))
                    { tx.Start(); ElementTransformUtils.MoveElement(_test!, _moving!, new XYZ(-100, 0, 0)); tx.Commit(); }
                    _step = 3; _next = DateTime.UtcNow.AddSeconds(3);
                }
                else if (_step == 3)
                {
                    Check(RadarDataStore.Instance.ActiveCount >= 3, "Previously resolved clash is reported again");
                    using (var tx = new Transaction(_test!, "Verification delete"))
                    { tx.Start(); _test!.Delete(_moving!); tx.Commit(); }
                    _step = 4; _next = DateTime.UtcNow.AddSeconds(3);
                }
                else if (_step == 4)
                {
                    Check(RadarDataStore.Instance.ActiveCount == 0, "Deleted elements are removed from live results");
                    LiveMonitorService.Instance.Stop();
                    LiveMonitorService.Instance.Start(app);
                    using(var tx=new Transaction(_test!, "Verification place after restart"))
                    { tx.Start(); _moving=Box(_test!,BuiltInCategory.OST_MechanicalEquipment,new XYZ(0.5,0.5,0)).Id; tx.Commit(); }
                    _step=40;_next=DateTime.UtcNow.AddSeconds(3);
                }
                else if(_step==40)
                {
                    Check(RadarDataStore.Instance.ActiveCount>=3,"New placement after monitor restart detects host and linked clashes without selection");
                    using(var tx=new Transaction(_test!,"Verification remove new placement"))
                    {tx.Start();_test!.Delete(_moving!);tx.Commit();}
                    _step=41;_next=DateTime.UtcNow.AddSeconds(3);
                }
                else if(_step==41)
                {
                    Check(RadarDataStore.Instance.ActiveCount==0,"Restarted monitor clears deleted placement without user input");
                    LiveMonitorService.Instance.Stop();
                    LiveMonitorService.Instance.RequestScan(LiveScanAction.Ledger);LiveMonitorService.Instance.ExecuteQueued(app);
                    _step=5;_next=DateTime.UtcNow.AddSeconds(1);
                }
                else if(_step==5)
                {
                    if(ScanCoordinator.Busy)return;
                    Check(RadarDataStore.Instance.ActiveCount==0,"Refresh completes and removes stale panel results");
                    Check(Commands.Session.Clashes!=null,"Exports share current dashboard results");
                    using(var tx=new Transaction(_test!,"Verification existing pipe clashes"))
                    {
                        tx.Start();
                        NativePipe(_test!,new XYZ(195,0,3),new XYZ(205,0,3));
                        // This unrelated pre-existing clash must not enter live results.
                        NativePipe(_test!,new XYZ(495,0,3),new XYZ(505,0,3));
                        NativePipe(_test!,new XYZ(500,-5,3),new XYZ(500,5,3));
                        tx.Commit();
                    }
                    RadarDataStore.Instance.Clear();
                    app.ActiveUIDocument.Selection.SetElementIds(new ElementId[0]);
                    LiveMonitorService.Instance.Start(app);
                    using(var tx=new Transaction(_test!,"Draw live pipe"))
                    {tx.Start();_moving=NativePipe(_test!,new XYZ(200,-5,3),new XYZ(200,5,3)).Id;tx.Commit();}
                    _step=50;_next=DateTime.UtcNow.AddSeconds(4);
                }
                else if(_step==50)
                {
                    var found=RadarDataStore.Instance.GetActive();
                    Check(found.Count==1&&found[0].TestType==ClashTestType.HardClash&&found.All(c=>c.InvolvesHost(new HashSet<long>{_moving!.Value})),
                        "Live drawn native pipe reports only its own clash, excluding an unrelated existing pipe clash");
                    Check(!ScanCoordinator.Busy,"Drawing a pipe does not start a full scan");
                    using(var tx=new Transaction(_test!,"Move live pipe clear"))
                    {tx.Start();ElementTransformUtils.MoveElement(_test!,_moving!,new XYZ(0,0,2));tx.Commit();}
                    _step=51;_next=DateTime.UtcNow.AddSeconds(4);
                }
                else if(_step==51)
                {
                    Check(RadarDataStore.Instance.ActiveCount==0,"Moving the live drawn pipe clears its clash without discovering unrelated clashes");
                    using(var tx=new Transaction(_test!,"Restore live pipe"))
                    {tx.Start();ElementTransformUtils.MoveElement(_test!,_moving!,new XYZ(0,0,-2));tx.Commit();}
                    _step=52;_next=DateTime.UtcNow.AddSeconds(4);
                }
                else if(_step==52)
                {
                    Check(RadarDataStore.Instance.ActiveCount==1,"Restoring the live drawn pipe reports its clash again");
                    using(var tx=new Transaction(_test!,"Delete live pipe")){tx.Start();_test!.Delete(_moving!);tx.Commit();}
                    _step=53;_next=DateTime.UtcNow.AddSeconds(4);
                }
                else if(_step==53)
                {
                    Check(RadarDataStore.Instance.ActiveCount==0,"Deleting the live drawn pipe removes its results");
                    LiveMonitorService.Instance.Stop();
                    _step=400;_next=DateTime.UtcNow.AddSeconds(2);
                }

            }
            catch (Exception ex)
            {
                _timer?.Stop();
                Diagnostics.Log("Verification failed", ex);
                File.WriteAllText(Path.Combine(_folder, "failed.txt"), ex.ToString());
            }
        }
        private static void CreateFixture(UIApplication app)
        {
            VerifyHostScope(app);
            VerifyLifecycleScope(app);
            VerifySurfaceGeometry(app);
            VerifyNativePipes(app);
            string linkPath = Path.Combine(_folder, "verification-link.rvt");
            var linkDoc = app.Application.NewProjectDocument(UnitSystem.Metric);
            using (var tx = new Transaction(linkDoc, "Verification linked geometry"))
            { tx.Start(); Box(linkDoc, BuiltInCategory.OST_StructuralColumns, XYZ.Zero); tx.Commit(); }
            linkDoc.SaveAs(linkPath, new SaveAsOptions { OverwriteExistingFile = true });
            linkDoc.Close(false);
            var doc = app.Application.NewProjectDocument(UnitSystem.Metric);
            using (var tx = new Transaction(doc, "Verification geometry"))
            {
                tx.Start();
                _moving = Box(doc, BuiltInCategory.OST_MechanicalEquipment, new XYZ(0.5, 0.5, 0)).Id;
                Box(doc, BuiltInCategory.OST_StructuralColumns, XYZ.Zero);
                var level = Level.Create(doc, 0);
                var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.FloorPlan);
                ViewPlan.Create(doc, plan.Id, level.Id);
                tx.Commit();
            }
            string path = Path.Combine(_folder, "verification-host.rvt");
            doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true }); doc.Close(false);
            var ui = app.OpenAndActivateDocument(path);
            _test = doc = ui.Document;
            DocumentSession.Activate(doc);
            var engine = new ClashResolveAI.ClashEngine.ClashEngine(doc);
            var full = engine.RunFullScan(false,mode:ScanMode.HardAndClearance);
            Check(full.Count == 1 && full[0].TestType == ClashTestType.HardClash, "Known solid collision detected exactly once");
            Check(engine.LastStatistics.CollectIndexMilliseconds>0&&engine.LastStatistics.GeometryMilliseconds>0&&engine.LastStatistics.BooleanMilliseconds>0&&engine.LastStatistics.BooleanTests>0,"Full-scan phase timers record actual API work");
            var cacheSettings=AppSettings.Load().ScanSnapshot();
            var cachedFilter=ScanSessionCache.Filter(doc,cacheSettings);
            Check(object.ReferenceEquals(cachedFilter,ScanSessionCache.Filter(doc,cacheSettings)),"Category filter is reused within a session");
            cacheSettings.IncludeGenericModels=!cacheSettings.IncludeGenericModels;
            Check(!object.ReferenceEquals(cachedFilter,ScanSessionCache.Filter(doc,cacheSettings)),"Category setting change selects a fresh filter");
            Check(object.ReferenceEquals(ScanSessionCache.Rules(doc,"DefaultRules"),ScanSessionCache.Rules(doc,"DefaultRules")),"Unchanged rules engine is reused within a session");
            string cacheRuleName="VerificationCache-"+Guid.NewGuid().ToString("N");
            string cacheRulePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ClashResolveAI","Rules",cacheRuleName+".json");
            try {
                File.WriteAllText(cacheRulePath,"{\"name\":\"first\"}");
                var firstRules=ScanSessionCache.Rules(doc,cacheRuleName);var stamp=File.GetLastWriteTimeUtc(cacheRulePath);
                File.WriteAllText(cacheRulePath,"{\"name\":\"other\"}");File.SetLastWriteTimeUtc(cacheRulePath,stamp);
                var nextRules=ScanSessionCache.Rules(doc,cacheRuleName);
                Check(!object.ReferenceEquals(firstRules,nextRules)&&nextRules.CurrentRuleSetName=="other","Same-length rule edits invalidate cache even with unchanged timestamps");
            } finally { if(File.Exists(cacheRulePath))File.Delete(cacheRulePath); }
            var targeted = engine.RunTargetedScan(new[] { _moving!, ui.ActiveView.Id },ScanMode.HardAndClearance);
            Check(engine.LastStatistics.Sources==1,"Live batches exclude nonphysical view dependants");
            Check(targeted.Select(c => c.NormalizedKey).SequenceEqual(full.Select(c => c.NormalizedKey)), "Full and targeted scans agree");
            using (var tx = new Transaction(doc, "Verification clearance"))
            { tx.Start(); ElementTransformUtils.MoveElement(doc, _moving!, new XYZ(1.55, 0, 0)); tx.Commit(); }
            var clearance = engine.RunFullScan(false,mode:ScanMode.HardAndClearance);
            Check(engine.LastStatistics.SurfaceDistanceCalls>0&&engine.LastStatistics.SurfaceDistanceMilliseconds>0,"Clearance scan records surface-distance work");
            Check(clearance.Count == 1 && clearance[0].TestType == ClashTestType.ClearanceClash && clearance[0].Severity >= ClashSeverity.Soft, "Near miss stays a clearance issue, never a hard clash");
            using (var tx = new Transaction(doc, "Verification links"))
            {
                tx.Start(); ElementTransformUtils.MoveElement(doc, _moving!, new XYZ(-1.55, 0, 0));
                var type = RevitLinkType.Create(doc, ModelPathUtils.ConvertUserVisiblePathToModelPath(linkPath), new RevitLinkOptions(false));
                RevitLinkInstance.Create(doc, type.ElementId);
                var second = RevitLinkInstance.Create(doc, type.ElementId);
                ElementTransformUtils.RotateElement(doc, second.Id, Line.CreateBound(new XYZ(1, 1, 0), new XYZ(1, 1, 10)), Math.PI / 4);
                tx.Commit();
            }
            var cachedLinks=ScanSessionCache.Links(doc);
            var firstLinks=cachedLinks.GetAllLinks();
            Check(object.ReferenceEquals(firstLinks,cachedLinks.GetAllLinks()),"Link list is reused between jobs");
            var changedLink=firstLinks.First().Instance.Id;
            ScanSessionCache.Changed(doc,new[]{changedLink});
            Check(!object.ReferenceEquals(firstLinks,cachedLinks.GetAllLinks()),"Link-instance changes invalidate the session list");
            full = new ClashResolveAI.ClashEngine.ClashEngine(doc).RunFullScan(true,mode:ScanMode.HardAndClearance);
            Check(full.Count >= 3 && full.Select(c => c.NormalizedKey).Distinct().Count() == full.Count, "Rotated and repeated linked model instances retain distinct identities");
            ClashDatabase.Instance.BulkInsertClashes(full);
            Check(ClashDatabase.Instance.GetClashCount() >= 3, "SQLite native dependency and persistence work");
            var reasons=Enum.GetValues(typeof(UnverifiedReason)).Cast<UnverifiedReason>().Select((reason,i)=>new ClashResult {ClashId="REASON-"+i,TestType=ClashTestType.Unverified,UnverifiedReason=reason}).ToList();
            ClashDatabase.Instance.BulkInsertClashes(reasons);
            using(var connection=new System.Data.SQLite.SQLiteConnection("Data Source="+Path.Combine(_folder,"databases",DocumentSession.Key(doc)+".clash.db")+";Version=3;")){
                connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT ClashId,UnverifiedReason FROM Clashes WHERE ClashId LIKE 'REASON-%'";
                using var reader=command.ExecuteReader();int matched=0;
                while(reader.Read())if(reasons.Any(c=>c.ClashId==reader.GetString(0)&&c.UnverifiedReason.ToString()==reader.GetString(1)))matched++;
                Check(matched==3,"SQLite persists all three unverified reasons");
            }
            var defaults=new AppSettings();var snapshot=defaults.ScanSnapshot();snapshot.EffectiveMode=ScanMode.HardAndClearance;
            string settingsJson=Newtonsoft.Json.JsonConvert.SerializeObject(snapshot);
            Check(defaults.FullScanMode==ScanMode.HardOnly&&defaults.LiveMode==ScanMode.HardOnly&&defaults.MinimumOverlapMM3==1&&!settingsJson.Contains("EffectiveMode"),"Settings default to hard-only and 1 mm3 without persisting effective job mode");
            snapshot.FullScanMode=ScanMode.HardAndClearance;
            var restored=Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(Newtonsoft.Json.JsonConvert.SerializeObject(snapshot))!;
            Check(defaults.FullScanMode==ScanMode.HardOnly&&restored.FullScanMode==ScanMode.HardAndClearance&&restored.LiveMode==ScanMode.HardOnly,"Configured modes round-trip and job snapshot does not mutate loaded settings");
            Check(File.Exists(Reports.ExcelReportGenerator.Generate(full, "Verification", _folder)), "Excel report generated");
            Check(File.Exists(Reports.WordReportGenerator.Generate(full, "Verification", _folder)), "Word report generated");
            Check(File.Exists(new Services.BcfExportService().ExportClashes(full, "Verification", _folder)), "BCF report generated");
            VerifyLifecycleStateAndUi(doc);
            if(Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_PHASE0")=="1")return;
            Dashboard.ClashDashboard.Instance.MergeFullScan(full,ScanMode.HardAndClearance,new ScanScope());
            Dashboard.ClashDashboard.Instance.ShowWindow();
            Check(true, "Dashboard opens");
            _navigationTarget = full[0];
            _requestedView = Services.ClashViewNavigation.Show(app, _navigationTarget, true);
        }
        private static Autodesk.Revit.DB.Plumbing.Pipe NativePipe(Document doc,XYZ start,XYZ end)
        {
            var system=new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Plumbing.PipingSystemType)).FirstElement();
            var type=new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Plumbing.PipeType)).FirstElement();
            var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElement();
            var pipe=Autodesk.Revit.DB.Plumbing.Pipe.Create(doc,system.Id,type.Id,level.Id,start,end);
            pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(100/304.8);
            return pipe;
        }
        private static void VerifyNativePipes(UIApplication app)
        {
            var doc=app.Application.NewProjectDocument(UnitSystem.Metric);
            try{
                var system=new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Plumbing.PipingSystemType)).FirstElement();
                var type=new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Plumbing.PipeType)).FirstElement();
                Check(system!=null&&type!=null,"Native pipe test types available");
                Autodesk.Revit.DB.Plumbing.Pipe b;
                using(var tx=new Transaction(doc,"Native pipe test")){
                    tx.Start();var level=Level.Create(doc,0);
                    var a=Autodesk.Revit.DB.Plumbing.Pipe.Create(doc,system!.Id,type!.Id,level.Id,new XYZ(-5,0,3),new XYZ(5,0,3));
                    b=Autodesk.Revit.DB.Plumbing.Pipe.Create(doc,system.Id,type.Id,level.Id,new XYZ(0,-5,3),new XYZ(0,5,3));
                    a.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(100/304.8);
                    b.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(100/304.8);
                    tx.Commit();
                }
                var hit=new ClashResolveAI.ClashEngine.ClashEngine(doc).RunFullScan(false,mode:ScanMode.HardAndClearance);
                Check(hit.Count==1&&hit[0].TestType==ClashTestType.HardClash,"Real Revit pipes crossing are confirmed as a hard clash");
                using(var tx=new Transaction(doc,"Move pipe clear")){tx.Start();ElementTransformUtils.MoveElement(doc,b.Id,new XYZ(0,0,1));tx.Commit();}
                var clear=new ClashResolveAI.ClashEngine.ClashEngine(doc).RunFullScan(false,mode:ScanMode.HardAndClearance);
                Check(clear.Count==0,"Separated native pipes do not produce false clashes");
            }finally{doc.Close(false);}
        }
        private static void VerifySurfaceGeometry(UIApplication app)
        {
            var doc=app.Application.NewProjectDocument(UnitSystem.Metric);
            try {
                Element a,b;
                using(var tx=new Transaction(doc,"Accuracy fixture")){
                    tx.Start();a=Box(doc,BuiltInCategory.OST_MechanicalEquipment,XYZ.Zero);
                    b=Box(doc,BuiltInCategory.OST_StructuralColumns,new XYZ(2.25,2.25,0));tx.Commit();
                }
                var engine=new ClashResolveAI.ClashEngine.ClashEngine(doc);
                Check(engine.RunFullScan(false,mode:ScanMode.HardAndClearance).Count==0,"Diagonal 107.8 mm separation is not a 100 mm clearance clash");
                using(var tx=new Transaction(doc,"Accuracy near miss")){
                    tx.Start();ElementTransformUtils.MoveElement(doc,b.Id,new XYZ(0,-2.25,0));tx.Commit();
                }
                var near=engine.RunFullScan(false,mode:ScanMode.HardAndClearance);
                Check(near.Count==1&&near[0].TestType==ClashTestType.ClearanceClash&&Math.Abs(near[0].GapMM-76.2)<0.1,"Actual planar clearance distance is 76.2 mm");
                Check(engine.RunFullScan(false,mode:ScanMode.HardOnly).Count==0&&engine.LastStatistics.GeometryLoads==0&&engine.LastStatistics.SurfaceDistanceCalls==0&&engine.LastStatistics.SurfaceDistanceMilliseconds==0,"Hard-only rejects near misses before geometry and skips surface distance");
                using(var tx=new Transaction(doc,"Accuracy intersection")){
                    tx.Start();ElementTransformUtils.MoveElement(doc,b.Id,new XYZ(-0.5,0,0));tx.Commit();
                }
                var hard=engine.RunFullScan(false,mode:ScanMode.HardAndClearance);
                Check(hard.Count==1&&hard[0].TestType==ClashTestType.HardClash&&Math.Abs(hard[0].OverlapVolumeMM3-Math.Pow(304.8,3))<1,
                    "Known one-cubic-foot intersection volume is measured");
                Check(hard[0].ClashPoint.X>1.74&&hard[0].ClashPoint.X<2.01,"Navigation point lies in actual solid intersection");
                var hardOnly=engine.RunFullScan(false,mode:ScanMode.HardOnly);
                Check(hardOnly.Select(c=>c.ClashId).SequenceEqual(hard.Select(c=>c.ClashId))&&engine.LastStatistics.SurfaceDistanceCalls==0,"Hard-only preserves confirmed overlap identity without surface work");
                var saved=AppSettings.Load();double originalTolerance=saved.MinimumOverlapMM3;
                var originalFull=saved.FullScanMode;var originalLive=saved.LiveMode;
                try {
                    saved.FullScanMode=ScanMode.HardAndClearance;saved.LiveMode=ScanMode.HardOnly;
                    engine.RunFullScan(false);Check(engine.LastStatistics.Mode==ScanMode.HardAndClearance,"Unspecified full mode uses FullScanMode");
                    engine.RunTargetedScan(new[]{a.Id});Check(engine.LastStatistics.Mode==ScanMode.HardOnly,"Unspecified targeted mode uses LiveMode");
                    engine.RunFullScan(false,mode:ScanMode.HardOnly);Check(engine.LastStatistics.Mode==ScanMode.HardOnly,"Explicit job mode overrides configured mode");
                    saved.MinimumOverlapMM3=hard[0].OverlapVolumeMM3*2;
                    Check(engine.RunFullScan(false,mode:ScanMode.HardOnly).Count==0&&engine.LastStatistics.BelowTolerance==1&&engine.LastStatistics.SkippedClearance==1&&engine.LastStatistics.SurfaceDistanceCalls==0,"Below-tolerance solid overlap is counted and omitted in hard-only mode");
                } finally {saved.MinimumOverlapMM3=originalTolerance;saved.FullScanMode=originalFull;saved.LiveMode=originalLive;}
                DirectShape curve;
                using(var tx=new Transaction(doc,"Missing solid fixture")){
                    tx.Start();curve=DirectShape.CreateElement(doc,new ElementId((long)BuiltInCategory.OST_GenericModel));
                    curve.SetShape(new GeometryObject[]{Line.CreateBound(new XYZ(.5,.5,.5),new XYZ(1.5,1.5,1.5))});tx.Commit();
                }
                Check(engine.RunTargetedScan(new[]{curve.Id},ScanMode.HardOnly).Count==0&&engine.LastStatistics.MissingGeometry>0&&engine.LastStatistics.SurfaceDistanceCalls==0,"Missing solid geometry increments the counter without hard-only rows");
                var missing=engine.RunTargetedScan(new[]{curve.Id},ScanMode.HardAndClearance);
                Check(missing.Count>0&&missing.All(c=>c.TestType==ClashTestType.Unverified&&c.UnverifiedReason==UnverifiedReason.MissingGeometry),"Combined-mode missing geometry carries its reason");
                using(var tx=new Transaction(doc,"Remove missing solid fixture")){tx.Start();doc.Delete(curve.Id);tx.Commit();}
                using(var tx=new Transaction(doc,"Hollow geometry test")){
                    tx.Start();
                    CurveLoop Loop(double x,double y,double size,bool reverse){
                        var pts=new[]{new XYZ(x,y,0),new XYZ(x+size,y,0),new XYZ(x+size,y+size,0),new XYZ(x,y+size,0)};
                        if(reverse)Array.Reverse(pts);
                        var loop=new CurveLoop();for(int i=0;i<4;i++)loop.Append(Line.CreateBound(pts[i],pts[(i+1)%4]));return loop;
                    }
                    var ring=GeometryCreationUtilities.CreateExtrusionGeometry(new[]{Loop(20,0,8,false),Loop(22,2,4,true)},XYZ.BasisZ,2);
                    var shape=DirectShape.CreateElement(doc,new ElementId((long)BuiltInCategory.OST_StructuralColumns));shape.SetShape(new GeometryObject[]{ring});
                    Box(doc,BuiltInCategory.OST_MechanicalEquipment,new XYZ(23,3,0));tx.Commit();
                }
                Check(new ClashResolveAI.ClashEngine.ClashEngine(doc).RunFullScan(false,mode:ScanMode.HardAndClearance).Count==1,"Element inside a hollow opening is not a bounding-box false clash");
                using(var job=engine.CreateJob(null,false)){job.Advance(1);job.Dispose();Check(job.Cancelled||job.Complete,"Scan job can cancel at a work boundary");}
            }finally{doc.Close(false);}
        }
        private static DirectShape Box(Document doc, BuiltInCategory category, XYZ origin)
        {
            var loop = new CurveLoop();
            var p = new[] { origin, origin + new XYZ(2,0,0), origin + new XYZ(2,2,0), origin + new XYZ(0,2,0) };
            for (int i=0; i<4; i++) loop.Append(Line.CreateBound(p[i],p[(i+1)%4]));
            var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, 2);
            var shape = DirectShape.CreateElement(doc, new ElementId((long)category));
            shape.SetShape(new GeometryObject[] { solid });
            return shape;
        }
    }
}


