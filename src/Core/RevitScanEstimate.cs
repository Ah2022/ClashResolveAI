using Autodesk.Revit.DB;
using BIMEngine.Core.Spatial;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Dashboard.Persistence;
using ClashResolveAI.Links;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Core
{
    internal static class RevitScanEstimate
    {
        public static string Environment(Document doc,ScanPlan plan)
        {
            var capture=RevitScanCapture.Capture(doc,new AppSettings(),plan.SelectedLinkInstanceIds.Count>0,"",null,plan.Mode,plan.LinkToLink,plan);
            return ProjectClassificationStore.Hash(capture.ModelFingerprint+"|"+ScanProfiles.Fingerprint(plan));
        }
        public static IReadOnlyList<ScanFloor> Floors(Document doc)=>new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l=>l.Elevation).Select(l=>new ScanFloor(l.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),l.Name,l.Elevation)).ToArray();
        public static ScanEstimateJob Create(Document doc,ScanPlan plan,IEnumerable<ScanTimingObservation>? suppliedTimings=null,int sourceBudget=512,int candidateBudget=100000)
        {
            ScanProfiles.Validate(plan);
            var stamp=Environment(doc,plan);
            return new ScanEstimateJob(job=>Collect(job,doc,plan,stamp,suppliedTimings,sourceBudget,candidateBudget));
        }
        private static IEnumerable<int> Collect(ScanEstimateJob job,Document doc,ScanPlan plan,string stamp,IEnumerable<ScanTimingObservation>? suppliedTimings,int sourceBudget,int candidateBudget)
        {
            var links=RevitScanPlanFactory.Links(doc);
            if(plan.SelectedLinkInstanceIds.Any(id=>!links.Any(l=>l.IsLoaded&&l.Instance.UniqueId==id)))throw new InvalidOperationException("A selected link is no longer loaded.");
            var needed=new HashSet<string>(plan.Relationships().SelectMany(p=>new[]{p.Source,p.Target}));
            var models=new List<EstimateModel>();var elements=new List<EstimateElement>();var classifier=new RevitElementClassifier(plan);
            var rawCounts=new Dictionary<string,long>();
            using var filter=ClashEngine.ClashEngine.BuildCategoryFilter(new AppSettings {IncludeGenericModels=plan.CandidatePolicy.IncludeGenericModels,
                IncludeInsulation=plan.CandidatePolicy.IncludeInsulation&&plan.Stage==ScanStage.InsulationClearance});
            var instances=new[]{Tuple.Create("","Host","",doc,Transform.Identity,true)}.Concat(links.Select(l=>Tuple.Create(l.Instance.UniqueId,
                l.FileName+" [instance "+l.Instance.Id.Value+"]",l.FileName,l.LinkDoc,l.Transform,l.IsLoaded))).ToArray();
            foreach(var instance in instances) {
                job.Progress="Inventory — "+instance.Item2;yield return 0;
                bool selected=needed.Contains(instance.Item1);int eligible=0,unknown=0,excluded=0;long? raw=null;
                if(instance.Item6) {
                    string documentKey=DocumentSession.Key(instance.Item4);
                    if(!rawCounts.TryGetValue(documentKey,out var count)) {
                        using var all=new FilteredElementCollector(instance.Item4);count=all.WhereElementIsNotElementType().GetElementCount();rawCounts[documentKey]=count;
                    }
                    raw=count;
                    if(selected) {
                        using var collector=new FilteredElementCollector(instance.Item4);
                        foreach(var id in collector.WherePasses(filter).WhereElementIsNotElementType().ToElementIds()) {
                            yield return 0;var element=instance.Item4.GetElement(id);if(element==null||!element.IsValidObject)continue;
                            var classification=classifier.Get(element);if(classification.Discipline==Discipline.Unknown)unknown++;
                            if(!ScanPlanEligibility.Includes(plan,classification)){excluded++;continue;}
                            var box=LinkedModelManager.GetTransformedBB(element,instance.Item5);if(box==null){excluded++;continue;}
                            string workset=plan.Zones.Any(z=>z.WorksetName!="")?element.Document.GetWorksetTable().GetWorkset(element.WorksetId)?.Name??"":"";
                            elements.Add(new EstimateElement(instance.Item1,id.Value,classification,new AABB(box.Min.X,box.Min.Y,box.Min.Z,box.Max.X,box.Max.Y,box.Max.Z),workset));eligible++;
                        }
                    }
                }
                models.Add(new EstimateModel(instance.Item1,instance.Item2,instance.Item3,instance.Item6,raw,eligible,unknown,excluded,selected));
            }
            var timings=suppliedTimings??Timings();
            foreach(var step in ScanEstimateCalculator.Calculate(job,plan,models,elements,Floors(doc),timings,sourceBudget,candidateBudget))yield return step;
            if(Environment(doc,plan)!=stamp)throw new InvalidOperationException("Model or link inputs changed while estimating. Recalculate the estimate.");
            job.Result!.EnvironmentFingerprint=stamp;
        }
        private static IReadOnlyList<ScanTimingObservation> Timings()
        {
            var result=new List<ScanTimingObservation>();
            foreach(var version in ClashDatabase.Instance.History.GetVersions().Where(v=>v.State==ScanVersionState.Completed).OrderByDescending(v=>v.SequenceNumber).Take(20)) {
                try {
                    var config=JObject.Parse(version.Capture.ConfigurationJson);var plan=config["Plan"]?.ToObject<ScanPlan>();if(plan==null)continue;
                    var stats=JObject.Parse(version.StatisticsJson);double Value(string key)=>(double?)stats[key]??0;
                    if(Value("IndexedElements")<=0||Value("WallMilliseconds")<=0)continue;
                    result.Add(new ScanTimingObservation(plan,Value("IndexedElements"),Value("SpatialQueries"),Value("Tested"),Value("CollectIndexMilliseconds"),
                        Value("CandidateMilliseconds"),Value("GeometryMilliseconds")+Value("BooleanMilliseconds")+Value("SurfaceDistanceMilliseconds"),Value("WallMilliseconds"),Value("WorkMilliseconds")));
                } catch(Exception ex){Diagnostics.Log("Prior scan cannot calibrate estimate",ex);}
            }
            return result;
        }
    }
}
