using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ClashResolveAI.ClashEngine;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClashResolveAI.Core
{
    // Opt-in verification only. Opens the local copy detached. Never saves it or
    // opens the user's source RVT as an editable host document.
    internal static class ProductionVerification
    {
        private static string _folder="";
        private static Document? _doc;
        private static ScanJob? _job;
        private static Stopwatch _elapsed=new Stopwatch();
        private static List<ClashResult> _baseline=new List<ClashResult>();
        private static List<ElementId> _sample=new List<ElementId>();
        private static int _phase;
        private static DateTime _write;
        public static void Attach(UIControlledApplication app)
        { /* Uses the real full-scan coordinator; no separate test idle scheduler. */ }
        public static void Begin(UIApplication app,string folder)
        {
            string path=Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_MODEL")??"";
            if(string.IsNullOrEmpty(path))return;
            _folder=folder;
            _phase=0;_write=DateTime.MinValue;
            string allowed=Path.GetFullPath(Path.Combine(folder,"..","production"))+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(path).StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Verification model must be a workspace production copy.");
            File.WriteAllText(Path.Combine(folder,"production-opening.txt"),path);
            var options=new OpenOptions { DetachFromCentralOption=DetachFromCentralOption.DetachAndPreserveWorksets };
            _doc=app.OpenAndActivateDocument(ModelPathUtils.ConvertUserVisiblePathToModelPath(path),options,false).Document;
            DocumentSession.Activate(_doc);
            var links=new FilteredElementCollector(_doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Select(l=>new {id=l.Id.Value,name=l.Name,loaded=l.GetLinkDocument()!=null,path=l.GetLinkDocument()?.PathName}).ToList();
            var categories=new FilteredElementCollector(_doc).WhereElementIsNotElementType().Where(e=>e.Category!=null)
                .GroupBy(e=>e.Category.Name).Select(g=>new {category=g.Key,count=g.Count()}).OrderByDescending(x=>x.count).ToList();
            File.WriteAllText(Path.Combine(folder,"production-inventory.json"),JsonConvert.SerializeObject(new {path=_doc.PathName,scanIncludesLinks=false,links,categories,settings=new {AppSettings.Load().ScanLinkedModels,AppSettings.Load().ScanWithinLinks,AppSettings.Load().MinimumOverlapMM3}},Formatting.Indented));
            _elapsed.Restart();
            // Phase 0 reference run is explicitly host-only; user settings are not changed.
            ScanCoordinator.StartJob(_doc,new ClashResolveAI.ClashEngine.ClashEngine(_doc,AppSettings.Load().RuleSetName).CreateJob(null,false,mode:ScanMode.HardAndClearance),Completed,Progress,Failed);
        }
        private static void Progress(string message)
        {
            if(DateTime.UtcNow<_write)return;
            File.WriteAllText(Path.Combine(_folder,"production-progress.txt"),$"Phase {_phase}; elapsed {_elapsed.Elapsed}; "+message);
            _write=DateTime.UtcNow.AddSeconds(2);
        }
        private static void Failed(string message) => File.WriteAllText(Path.Combine(_folder,"production-failed.txt"),message);
        private static void Completed(List<ClashResult> results,ScanStatistics stats)
        {
            try {
                if(_phase==0){
                    _baseline=results;
                    File.WriteAllText(Path.Combine(_folder,"production-scan.json"),JsonConvert.SerializeObject(new {
                        wallSeconds=_elapsed.Elapsed.TotalSeconds,statistics=stats,
                        hard=_baseline.Count(c=>c.TestType==ClashTestType.HardClash),
                        clearance=_baseline.Count(c=>c.TestType==ClashTestType.ClearanceClash),
                        unverified=_baseline.Count(c=>c.TestType==ClashTestType.Unverified),
                        results=_baseline.Select(c=>new {c.ClashId,c.ElementAId,c.ElementBId,c.LinkInstanceA,c.LinkInstanceB,c.TestType,c.UnverifiedReason,c.Severity,c.GeometryEvidence,c.GapMM,c.OverlapVolumeMM3,c.RequiredClearanceMM,c.CategoryNameA,c.CategoryNameB,c.FamilyTypeA,c.FamilyTypeB})
                    },Formatting.Indented));
                    var ids=new FilteredElementCollector(_doc!).WhereElementIsNotElementType().Where(e=>e is MEPCurve).Select(e=>e.Id).ToList();
                    _sample=ids.Where((id,i)=>i%Math.Max(1,ids.Count/10)==0).Take(10).ToList();
                    _phase=1;
                    _job=new ClashResolveAI.ClashEngine.ClashEngine(_doc!,AppSettings.Load().RuleSetName).CreateJob(_sample,false,mode:ScanMode.HardAndClearance);
                    _elapsed.Restart();
                    ScanCoordinator.StartJob(_doc!,_job,Completed,Progress,Failed);return;
                }
                if(_phase==2){
                    var smallest=_baseline.Where(c=>c.TestType==ClashTestType.HardClash).OrderBy(c=>c.OverlapVolumeMM3).ThenBy(c=>c.ClashId).Take(10).ToList();
                    var actualSmall=results.Where(c=>smallest.Any(b=>b.ClashId==c.ClashId)).OrderBy(c=>c.OverlapVolumeMM3).ThenBy(c=>c.ClashId).ToList();
                    bool pass=actualSmall.Count==smallest.Count&&actualSmall.All(c=>smallest.Any(b=>b.ClashId==c.ClashId&&b.TestType==c.TestType&&b.OverlapVolumeMM3==c.OverlapVolumeMM3));
                    File.WriteAllText(Path.Combine(_folder,"production-small-overlaps.json"),JsonConvert.SerializeObject(new {pass,statistics=stats,results=actualSmall.Select(c=>new {c.ClashId,c.ElementAId,c.ElementBId,c.CategoryNameA,c.CategoryNameB,c.FamilyTypeA,c.FamilyTypeB,c.OverlapVolumeMM3})},Formatting.Indented));
                    if(!pass)throw new InvalidOperationException("Small-overlap targeted/full mismatch.");
                    _phase=3;_elapsed.Restart();
                    _job=new ClashResolveAI.ClashEngine.ClashEngine(_doc!,AppSettings.Load().RuleSetName).CreateJob(null,false,mode:ScanMode.HardOnly);
                    ScanCoordinator.StartJob(_doc!,_job,Completed,Progress,Failed);return;
                }
                if(_phase==3){
                    var expectedHard=new HashSet<string>(_baseline.Where(c=>c.TestType==ClashTestType.HardClash).Select(c=>c.ClashId));
                    var expectedPossible=new HashSet<string>(_baseline.Where(c=>c.TestType==ClashTestType.Unverified&&c.UnverifiedReason==UnverifiedReason.SolidTest).Select(c=>c.ClashId));
                    var actualHard=new HashSet<string>(results.Where(c=>c.TestType==ClashTestType.HardClash).Select(c=>c.ClashId));
                    var actualPossible=new HashSet<string>(results.Where(c=>c.TestType==ClashTestType.Unverified&&c.UnverifiedReason==UnverifiedReason.SolidTest&&c.GeometryEvidence.StartsWith("Possible hard: ")).Select(c=>c.ClashId));
                    bool pass=expectedHard.SetEquals(actualHard)&&expectedPossible.SetEquals(actualPossible)&&results.Count==actualHard.Count+actualPossible.Count&&stats.SurfaceDistanceCalls==0&&stats.SurfaceDistanceMilliseconds==0;
                    File.WriteAllText(Path.Combine(_folder,"production-hard-only.json"),JsonConvert.SerializeObject(new {
                        pass,wallSeconds=_elapsed.Elapsed.TotalSeconds,statistics=stats,hard=actualHard.Count,possibleHard=actualPossible.Count,
                        clearance=results.Count(c=>c.TestType==ClashTestType.ClearanceClash),unverified=results.Count(c=>c.TestType==ClashTestType.Unverified),
                        missingHard=expectedHard.Except(actualHard),extraHard=actualHard.Except(expectedHard),missingPossible=expectedPossible.Except(actualPossible),extraPossible=actualPossible.Except(expectedPossible),
                        results=results.Select(c=>new {c.ClashId,c.ElementAId,c.ElementBId,c.TestType,c.UnverifiedReason,c.GeometryEvidence,c.OverlapVolumeMM3})
                    },Formatting.Indented));
                    if(!pass)throw new InvalidOperationException("Hard-only baseline agreement or zero-surface-work check failed.");
                    IntegrationVerification.VerifyHostScopeExperiment(_doc!,_folder,"phase3-production");
                    File.WriteAllText(Path.Combine(_folder,"production-complete.txt"),"PASS: combined full/targeted scans agree; hard-only preserves all hard and Boolean-failure pairs with zero surface calls. Original model unchanged.");
                    _job=null;return;
                }
                var changed=new HashSet<long>(_sample.Select(i=>i.Value));
                var expected=new HashSet<string>(_baseline.Where(c=>c.InvolvesHost(changed)).Select(c=>c.NormalizedKey));
                var actual=new HashSet<string>(results.Select(c=>c.NormalizedKey));
                File.WriteAllText(Path.Combine(_folder,"production-targeted.json"),JsonConvert.SerializeObject(new {
                    pass=expected.SetEquals(actual),sample=_sample.Select(i=>i.Value),wallSeconds=_elapsed.Elapsed.TotalSeconds,
                    statistics=stats,expected=expected.Count,actual=actual.Count,missing=expected.Except(actual),extra=actual.Except(expected)
                },Formatting.Indented));
                if(!expected.SetEquals(actual))throw new InvalidOperationException("Production full/targeted result mismatch.");
                _sample=_baseline.Where(c=>c.TestType==ClashTestType.HardClash).OrderBy(c=>c.OverlapVolumeMM3).ThenBy(c=>c.ClashId).Take(10)
                    .SelectMany(c=>new[]{c.ElementAId,c.ElementBId}).Distinct().Select(id=>new ElementId(id)).ToList();
                _phase=2;_elapsed.Restart();
                _job=new ClashResolveAI.ClashEngine.ClashEngine(_doc!,AppSettings.Load().RuleSetName).CreateJob(_sample,false,mode:ScanMode.HardAndClearance);
                ScanCoordinator.StartJob(_doc!,_job,Completed,Progress,Failed);
            }catch(Exception ex){_job=null;File.WriteAllText(Path.Combine(_folder,"production-failed.txt"),ex.ToString());}
        }
    }
}
