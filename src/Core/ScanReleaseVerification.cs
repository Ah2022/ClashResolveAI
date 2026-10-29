using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.ClashEngine;
using ClashResolveAI.Dashboard;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Links;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClashResolveAI.Core
{
    internal static partial class IntegrationVerification
    {
        private static ScanPlan? _releaseDetailed;
        private static string _releaseError="";
        private static bool _releaseFinished;
        private static readonly List<string> ReleaseProgress=new List<string>();
        private static void PrepareScanRelease(UIApplication app)
        {
            var previous=app.ActiveUIDocument.Document;
            var fixture=app.Application.NewProjectDocument(UnitSystem.Metric);
            void Typed(BuiltInCategory category,string name,XYZ p){var e=Box(fixture,category,p);e.SetTypeId(DirectShapeType.Create(fixture,name,new ElementId((long)category)).Id);}
            using(var tx=new Transaction(fixture,"Staged coordination fixture")) {
                tx.Start();Typed(BuiltInCategory.OST_MechanicalEquipment,"AHU-HARD",XYZ.Zero);Typed(BuiltInCategory.OST_StructuralColumns,"COLUMN-HARD",new XYZ(.5,.5,0));
                Typed(BuiltInCategory.OST_MechanicalEquipment,"AHU-NEAR",new XYZ(4,0,0));Typed(BuiltInCategory.OST_StructuralColumns,"COLUMN-NEAR",new XYZ(6.25,0,0));
                Typed(BuiltInCategory.OST_MechanicalEquipment,"AHU-FAR",new XYZ(100,0,0));Typed(BuiltInCategory.OST_StructuralColumns,"COLUMN-FAR",new XYZ(102.25,0,0));
                tx.Commit();
            }
            string path=Path.Combine(_folder,"staged-host.rvt");fixture.SaveAs(path);fixture.Close(false);
            _test=app.OpenAndActivateDocument(path).Document;previous.Close(false);DocumentSession.Activate(_test);
            var level=new FilteredElementCollector(_test).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>Math.Abs(l.Elevation)).First();
            var basis=RevitScanPlanFactory.Create(_test,new AppSettings(),Array.Empty<LinkedModel>(),false);
            _releaseDetailed=ScanProfiles.Create(basis,new ScanProfileSelection {Profile=ScanProfile.DetailedCoordination,HostToLinks=false,LevelIds=new List<string>{level.Id.Value.ToString()},ClearanceRules=new List<string>{"Duct_vs_Beam"},CoordinationRadiusMM=2000});
            _releaseFinished=false;_releaseError="";
            ScanCoordinator.Start(_test,_releaseDetailed,(rows,stats)=>_releaseFinished=true,text=>ReleaseProgress.Add(text),reason=>_releaseError=reason);
            _step=1801;_next=DateTime.UtcNow.AddMilliseconds(250);
        }
        private static void ScanReleaseStep(UIApplication app)
        {
            if(ScanCoordinator.Busy){_next=DateTime.UtcNow.AddMilliseconds(250);return;}
            if(_step==1801) {
                Check(_releaseFinished&&_releaseError=="","Detailed workflow completes both native stages: "+_releaseError);
                var versions=ClashDatabase.Instance.History.GetVersions().Where(v=>v.State==ScanVersionState.Completed).OrderBy(v=>v.SequenceNumber).ToList();
                Check(versions.Count==2&&versions[0].Capture.ScanMode==ScanMode.HardOnly.ToString()&&versions[1].Capture.ScanMode==ScanMode.HardAndClearance.ToString(),"Hard results are saved as a completed version before localized clearance");
                var rows=ClashDashboard.Instance.Clashes;
                Check(rows.Any(c=>c.TestType==ClashTestType.HardClash)&&rows.Count(c=>c.TestType==ClashTestType.ClearanceClash)==1,"Staged scan retains hard overlaps and finds only the nearby 76.2 mm clearance issue");
                Check(rows.All(c=>c.ClashPoint.X<50),"Far clearance geometry outside confirmed areas never enters staged findings");
                Check(ReleaseProgress.Any(p=>p.Contains("Stage 2/2"))&&ReleaseProgress.Any(p=>p.Contains("Boolean tests:"))&&ReleaseProgress.Any(p=>p.Contains("Current floor:")),"Coordinator progress exposes stage, relationship, floor and geometric counters");
                var progress=new Commands.ProgressDialog("Native coordinator progress");progress.Show();progress.SetMessage(ReleaseProgress.Last());CaptureLifecycleUi(progress,"coordinator-progress.png");progress.Close();
                _releaseFinished=false;_releaseError="";
                ScanCoordinator.Start(_test!,_releaseDetailed!,(_,__)=>_releaseFinished=true,text=>{ReleaseProgress.Add(text);if(text.Contains("Stage 2/2"))ScanCoordinator.Cancel("Verification cancelled only clearance");},reason=>_releaseError=reason);
                _step=1802;_next=DateTime.UtcNow.AddMilliseconds(250);return;
            }
            if(_step==1802) {
                var versions=ClashDatabase.Instance.History.GetVersions().OrderBy(v=>v.SequenceNumber).ToList();
                Check(!_releaseFinished&&_releaseError.Contains("only clearance")&&versions.Last().State==ScanVersionState.Cancelled,"Cancelling stage two records cancellation and never publishes partial clearance");
                Check(versions[versions.Count-2].State==ScanVersionState.Completed&&ClashDashboard.Instance.Clashes.Any(c=>c.TestType==ClashTestType.HardClash),"Cancelling clearance retains the separately saved hard-clash stage");
                File.WriteAllText(Path.Combine(_folder,"staged-progress.json"),JsonConvert.SerializeObject(ReleaseProgress,Formatting.Indented));
                File.WriteAllText(Path.Combine(_folder,"staged-versions.json"),JsonConvert.SerializeObject(versions,Formatting.Indented));
                VerifyStagedBenchmark(app);_test!.Save();_timer?.Stop();
                File.WriteAllText(Path.Combine(_folder,"complete.txt"),"PASS "+Results.Count+" full scan release native checks. Production loaded-model acceptance remains a separate gate.");
            }
        }
        private static void VerifyStagedBenchmark(UIApplication app)
        {
            var doc=app.Application.NewProjectDocument(UnitSystem.Metric);var measurements=new List<object>();
            try {
                using(var tx=new Transaction(doc,"Loaded-link benchmark fixture")) {
                    tx.Start();for(int i=0;i<250;i++)Box(doc,BuiltInCategory.OST_MechanicalEquipment,new XYZ(i*5,0,0));
                    var path=Path.Combine(_folder,"estimate-link.rvt");var type=RevitLinkType.Create(doc,ModelPathUtils.ConvertUserVisiblePathToModelPath(path),new RevitLinkOptions(false));
                    RevitLinkInstance.Create(doc,type.ElementId);var repeated=RevitLinkInstance.Create(doc,type.ElementId);ElementTransformUtils.MoveElement(doc,repeated.Id,new XYZ(.25,.25,0));tx.Commit();
                }
                var plan=RevitScanPlanFactory.Create(doc,new AppSettings(),new LinkedModelManager(doc).GetLoadedLinks(),true);
                var clock=System.Diagnostics.Stopwatch.StartNew();
                using var estimate=RevitScanEstimate.Create(doc,plan,Array.Empty<ScanTimingObservation>(),10000,1000000);
                while(!estimate.Advance(25)){}clock.Stop();var estimateMS=clock.Elapsed.TotalMilliseconds;
                foreach(bool native in new[]{false,true}) {
                    var engine=new ClashResolveAI.ClashEngine.ClashEngine(doc);using var job=engine.CreatePlannedJob(plan,native);clock.Restart();while(!job.Advance(25)){}clock.Stop();
                    Check(job.Statistics.Candidates==estimate.Result!.BoundingBoxPairs,"Loaded benchmark complete bounding-box estimate agrees with "+(native?"native":"octree")+" spatial queries");
                    measurements.Add(new {native,estimateMS,wallMS=clock.Elapsed.TotalMilliseconds,job.Statistics,rows=job.Results.Select(c=>c.NormalizedKey).OrderBy(k=>k).ToArray()});
                }
                File.WriteAllText(Path.Combine(_folder,"loaded-fixture-benchmark.json"),JsonConvert.SerializeObject(new {ModelKind="Controlled fixture, not production",HostSources=250,LoadedInstances=2,measurements},Formatting.Indented));
            } finally {doc.Close(false);}
        }
    }
}
