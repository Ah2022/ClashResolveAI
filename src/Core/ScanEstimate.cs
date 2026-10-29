using BIMEngine.Core.Spatial;
using ClashResolveAI.Rules;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ClashResolveAI.Core
{
    public sealed class EstimateModel
    {
        public string Id { get; }
        public string Name { get; }
        public string FileName { get; }
        public bool Loaded { get; }
        public long? Elements { get; }
        public int Eligible { get; }
        public int Unknown { get; }
        public int Excluded { get; }
        public bool Selected { get; }
        public EstimateModel(string id,string name,string fileName,bool loaded,long? elements,int eligible=0,int unknown=0,int excluded=0,bool selected=true)
        {Id=id;Name=name;FileName=fileName;Loaded=loaded;Elements=elements;Eligible=eligible;Unknown=unknown;Excluded=excluded;Selected=selected;}
        public string ElementCount=>Elements.HasValue?Elements.Value.ToString("N0"):"Unavailable";
        public string EligibleCount=>!Loaded?"Unavailable":!Selected?"Not selected":Eligible.ToString("N0");
        public string Status=>Id==""?"Host":!Loaded?"Unloaded":Selected?"Loaded / selected":"Loaded / not selected";
        public string UnknownCount=>!Loaded?"Unavailable":!Selected?"Not inventoried":Unknown.ToString("N0");
        public string ExcludedCount=>!Loaded?"Unavailable":!Selected?"Not inventoried":Excluded.ToString("N0");
    }
    public sealed class EstimateElement
    {
        public string ModelId { get; }
        public long ElementId { get; }
        public ClassifiedPartition Partition { get; }
        public AABB Bounds { get; }
        public string Workset { get; }
        public EstimateElement(string modelId,long elementId,ElementClassification classification,AABB bounds,string workset="")
        {ModelId=modelId;ElementId=elementId;Partition=new ClassifiedPartition(classification);Bounds=bounds;Workset=workset;}
    }
    public sealed class EstimatePair
    {
        public ScanModelPair Pair { get; internal set; }=null!;
        public string Label { get; internal set; }="";
        public long PossibleRelationships { get; internal set; }
        public double BoundingBoxPairs { get; internal set; }
        public double BoundingBoxLow { get; internal set; }
        public double BoundingBoxHigh { get; internal set; }
        public double GeometryPairs { get; internal set; }
        public int Samples { get; internal set; }
        public int SourceElements { get; internal set; }
        public bool Complete { get; internal set; }
        public bool Capped { get; internal set; }
        public long SpatialQueries { get; internal set; }
        public int ClearancePartitions { get; internal set; }
        public double? DurationLowSeconds { get; internal set; }
        public double? DurationHighSeconds { get; internal set; }
        public string CandidateRange=>$"{BoundingBoxLow:N0}–{BoundingBoxHigh:N0}";
        public bool StagedUpperBound { get; internal set; }
        public string Confidence=>StagedUpperBound?"Two-pass upper bound":Capped?"Dense sample capped":Complete?"Complete box count":Samples==0?"Not sampled":"Sampled boxes";
        public string Duration=>ScanEstimate.FormatDuration(DurationLowSeconds,DurationHighSeconds);
    }
    public sealed class ScanEstimate
    {
        public string PlanFingerprint { get; internal set; }="";
        public string EnvironmentFingerprint { get; internal set; }="";
        public IReadOnlyList<EstimateModel> Models { get; internal set; }=Array.Empty<EstimateModel>();
        public IReadOnlyList<EstimatePair> Pairs { get; internal set; }=Array.Empty<EstimatePair>();
        public IReadOnlyList<string> DisciplinePairs { get; internal set; }=Array.Empty<string>();
        public IReadOnlyList<string> Warnings { get; internal set; }=Array.Empty<string>();
        public long PossibleRelationships=>Pairs.Aggregate(0L,(sum,p)=>checked(sum+p.PossibleRelationships));
        public double BoundingBoxPairs=>Pairs.Sum(p=>p.BoundingBoxPairs);
        public int SourceElements { get; internal set; }
        public int CalibrationScans { get; internal set; }
        public double? DurationLowSeconds { get; internal set; }
        public double? DurationHighSeconds { get; internal set; }
        public string Duration=>FormatDuration(DurationLowSeconds,DurationHighSeconds);
        public static string FormatDuration(double? low,double? high)
        {
            if(!low.HasValue||!high.HasValue)return "Unavailable — insufficient comparable timings";
            string Value(double seconds)=>seconds>=3600?$"{seconds/3600:N1} h":seconds>=60?$"{seconds/60:N1} min":$"{Math.Max(1,seconds):N0} s";
            return Value(low.Value)+"–"+Value(high.Value);
        }
    }
    public sealed class ScanTimingObservation
    {
        public string Key { get; }
        public double CollectPerElementMS { get; }
        public double QueryMS { get; }
        public double VerifyMS { get; }
        public double WallFactor { get; }
        public bool HasGeometryRate { get; }
        public ScanTimingObservation(ScanPlan plan,double indexed,double queries,double tested,double collectMS,double candidateMS,double verificationMS,double wallMS,double workMS)
        {
            Key=TimingKey(plan);CollectPerElementMS=collectMS/Math.Max(1,indexed);QueryMS=candidateMS/Math.Max(1,queries);
            VerifyMS=verificationMS/Math.Max(1,tested);HasGeometryRate=tested>0&&verificationMS>0;
            WallFactor=Math.Max(1,Math.Min(20,wallMS/Math.Max(1,workMS)));
        }
        public static string TimingKey(ScanPlan plan)=>ProjectClassificationStore.Hash(JsonConvert.SerializeObject(new {Algorithm=2,plan.Mode,Stage=plan.Stage==ScanStage.Clearance?ScanStage.Coordination:plan.Stage,plan.RuleSetContent,
            plan.CandidatePolicy,plan.MinimumOverlapMM3,plan.InsulationMM,plan.MaintenanceMM,plan.ExcludeConnectedJoints,plan.ClearanceRuleKeys}));
        internal double Seconds(double elements,double queries,double geometry)=>(elements*CollectPerElementMS+queries*QueryMS+geometry*VerifyMS)*WallFactor/1000;
    }
    public sealed class ScanEstimateJob : IDisposable
    {
        private readonly IEnumerator<int> _work;
        public ScanEstimate? Result { get; internal set; }
        public string Progress { get; internal set; }="Preparing estimate…";
        public bool Complete { get; private set; }
        public bool Cancelled { get; private set; }
        public long CandidateVisits { get; internal set; }
        internal ScanEstimateJob(Func<ScanEstimateJob,IEnumerable<int>> work){_work=work(this).GetEnumerator();}
        public bool Advance(int milliseconds=25)
        {
            if(Complete||Cancelled)return true;var timer=Stopwatch.StartNew();
            do {if(!_work.MoveNext()){Complete=true;return true;}}while(timer.ElapsedMilliseconds<Math.Max(1,milliseconds));return false;
        }
        public void Dispose(){if(!Complete)Cancelled=true;_work.Dispose();}
    }
    public static class ScanEstimateCalculator
    {
        private sealed class Partition
        {
            public ClassifiedPartition Descriptor=null!;public List<EstimateElement> Elements=new List<EstimateElement>();public Octree Index=null!;
        }
        public static ScanEstimateJob Create(ScanPlan plan,IReadOnlyList<EstimateModel> models,IReadOnlyList<EstimateElement> elements,
            IReadOnlyList<ScanFloor> floors,IEnumerable<ScanTimingObservation>? timings=null,int sourceBudget=512,int candidateBudget=100000)
            =>new ScanEstimateJob(job=>Calculate(job,plan,models,elements,floors,timings??Array.Empty<ScanTimingObservation>(),sourceBudget,candidateBudget));
        internal static IEnumerable<int> Calculate(ScanEstimateJob job,ScanPlan plan,IReadOnlyList<EstimateModel> models,IReadOnlyList<EstimateElement> elements,
            IReadOnlyList<ScanFloor> floors,IEnumerable<ScanTimingObservation> timings,int sourceBudget,int candidateBudget)
        {
            if(sourceBudget<1||candidateBudget<1)throw new ArgumentOutOfRangeException(nameof(sourceBudget));
            var scope=new ScanSpatialScope(plan,floors);var policy=new CompiledPairPolicy(plan);var modelLookup=models.ToDictionary(m=>m.Id);
            var relationships=plan.Relationships();var sourcesByPair=new List<List<EstimateElement>>();
            string Name(string id)=>id==""?"":modelLookup[id].FileName;
            var byModel=elements.GroupBy(e=>e.ModelId).ToDictionary(g=>g.Key,g=>g.ToList());
            List<EstimateElement> Elements(string id)=>byModel.TryGetValue(id,out var list)?list:new List<EstimateElement>();
            AABB? envelope=null;var uniqueSources=new HashSet<string>();
            foreach(var pair in relationships) {
                var sources=new List<EstimateElement>();
                foreach(var element in Elements(pair.Source)) {
                    yield return 0;
                    if(!scope.Includes(element.Bounds,Name(pair.Source),Name(pair.Target),element.Workset))continue;
                    sources.Add(element);uniqueSources.Add(element.ModelId+":"+element.ElementId);
                    envelope=envelope.HasValue?envelope.Value.Union(element.Bounds):element.Bounds;
                }
                sourcesByPair.Add(sources);
            }
            var rules=JsonConvert.DeserializeObject<RuleSet>(plan.RuleSetContent)!;
            double maxRule=rules.Rules.Values.Concat(rules.CategoryPairs.Values).Concat(rules.DisciplinePairs.Values).Select(r=>r.ClearanceMM/304.8).DefaultIfEmpty(0).Max();
            if(envelope.HasValue)envelope=envelope.Value.Expand(plan.Mode==ScanMode.HardOnly?1/304.8:Math.Max(.5,Math.Max(maxRule,Math.Max(plan.InsulationMM,plan.MaintenanceMM)/304.8)));
            var partitions=new Dictionary<string,List<Partition>>();var indexedCount=0;
            foreach(var model in models.Where(m=>m.Selected)) {
                job.Progress="Indexing estimate boxes — "+model.Name;
                var groups=new Dictionary<string,Partition>();
                foreach(var element in Elements(model.Id)) {
                    yield return 0;if(!plan.WholeModelScope&&(!envelope.HasValue||!element.Bounds.Intersects(envelope.Value)))continue;
                    if(!groups.TryGetValue(element.Partition.Key,out var group))groups[element.Partition.Key]=group=new Partition {Descriptor=element.Partition};
                    group.Elements.Add(element);indexedCount++;
                }
                foreach(var group in groups.Values) {
                    var box=group.Elements[0].Bounds;foreach(var element in group.Elements)box=box.Union(element.Bounds);
                    group.Index=new Octree(box.Expand(.5));
                    for(int i=0;i<group.Elements.Count;i++){group.Index.Insert(new SpatialEntry(i,group.Elements[i].Bounds));yield return 0;}
                }
                partitions[model.Id]=groups.Values.ToList();
            }
            var rows=new List<EstimatePair>();var allowedDisciplines=new HashSet<string>();var timingKey=ScanTimingObservation.TimingKey(plan);
            var comparable=timings.Where(t=>t.Key==timingKey).ToArray();
            for(int r=0;r<relationships.Count;r++) {
                var pair=relationships[r];var sources=sourcesByPair[r];var targets=partitions[pair.Target];
                var row=new EstimatePair {Pair=pair,Label=modelLookup[pair.Source].Name+" ↔ "+modelLookup[pair.Target].Name,SourceElements=sources.Count};
                foreach(var source in sources.GroupBy(s=>s.Partition.Key))foreach(var target in targets) {
                    var decision=policy.Resolve(source.First().Partition,target.Descriptor);if(!decision.Allowed)continue;
                    row.PossibleRelationships=checked(row.PossibleRelationships+(long)source.Count()*target.Elements.Count-
                        (pair.Source==pair.Target&&source.Key==target.Descriptor.Key?source.Count():0));
                    row.SpatialQueries=checked(row.SpatialQueries+source.Count());if(decision.ClearanceEnabled)row.ClearancePartitions++;
                    var a=source.First().Partition.Discipline.ToString();var b=target.Descriptor.Discipline.ToString();
                    allowedDisciplines.Add(string.CompareOrdinal(a,b)<=0?a+" ↔ "+b:b+" ↔ "+a);
                }
                rows.Add(row);yield return 0;
            }
            int remaining=sourceBudget;var sorted=rows.Select((row,i)=>new {Row=row,Index=i}).OrderByDescending(x=>x.Row.PossibleRelationships).ToArray();
            for(int position=0;position<sorted.Length;position++) {
                var item=sorted[position];var row=item.Row;var pair=row.Pair;var sources=sourcesByPair[item.Index];var targets=partitions[pair.Target];
                int limit=Math.Min(sources.Count,remaining==0?0:Math.Max(1,remaining/(sorted.Length-position)));
                var random=new Random(1977+item.Index);var sample=new List<EstimateElement>(limit);
                for(int i=0;i<sources.Count;i++){if(i<limit)sample.Add(sources[i]);else if(limit>0){int slot=random.Next(i+1);if(slot<limit)sample[slot]=sources[i];}}
                var sourceIds=new HashSet<long>(sources.Select(s=>s.ElementId));double hits=0,geometry=0,observed=0;
                foreach(var source in sample) {
                    job.Progress="Sampling bounding boxes — "+row.Label;double sourceHits=0,sourceGeometry=0;bool capped=false;
                    foreach(var target in targets) {
                        var decision=policy.Resolve(source.Partition,target.Descriptor);if(!decision.Allowed)continue;
                        double threshold=Math.Max(decision.ClearanceMM,Math.Max(plan.InsulationMM,plan.MaintenanceMM))/304.8;
                        double radius=decision.ClearanceEnabled?threshold:1/304.8;
                        foreach(var candidate in target.Index.Enumerate(source.Bounds.Expand(radius))) {
                            if(job.CandidateVisits>=candidateBudget){capped=true;break;}job.CandidateVisits++;
                            var element=target.Elements[(int)candidate.ElementId];
                            if(pair.Source==pair.Target&&source.ElementId==element.ElementId)continue;
                            if(!ScanStaging.AllowsPair(plan,source.Bounds,element.Bounds))continue;
                            double weight=1;
                            if(pair.Source==pair.Target&&sourceIds.Contains(element.ElementId)) {
                                var reverse=policy.Resolve(element.Partition,source.Partition);
                                double reverseRadius=reverse.ClearanceEnabled?Math.Max(reverse.ClearanceMM,Math.Max(plan.InsulationMM,plan.MaintenanceMM))/304.8:1/304.8;
                                if(reverse.Allowed&&element.Bounds.Expand(reverseRadius).Intersects(source.Bounds))weight=.5;
                            }
                            sourceHits+=weight;if(decision.ClearanceEnabled?Distance(source.Bounds,element.Bounds)<=threshold:source.Bounds.Intersects(element.Bounds))sourceGeometry+=weight;
                            if(job.CandidateVisits%32==0)yield return 0;
                        }
                        if(capped)break;
                    }
                    observed+=sourceHits;
                    if(capped){row.Capped=true;break;}
                    hits+=sourceHits;geometry+=sourceGeometry;row.Samples++;remaining--;yield return 0;
                }
                row.Complete=row.Samples==sources.Count&&!row.Capped;
                row.BoundingBoxPairs=row.Samples==0?observed:hits*sources.Count/row.Samples;
                row.GeometryPairs=row.Samples==0?observed:geometry*sources.Count/row.Samples;
                row.BoundingBoxLow=row.Complete?row.BoundingBoxPairs:Math.Min(row.BoundingBoxPairs,observed);
                row.BoundingBoxHigh=row.Complete?row.BoundingBoxPairs:Math.Min(row.PossibleRelationships,Math.Max(row.BoundingBoxPairs*2,3.0*Math.Max(1,row.PossibleRelationships)/Math.Max(1,row.Samples)));
                if(row.Capped||row.Samples==0)row.BoundingBoxHigh=row.PossibleRelationships;
                var rates=comparable.Where(t=>(row.Complete&&row.GeometryPairs==0)||row.BoundingBoxHigh==0||t.HasGeometryRate).ToArray();
                if(rates.Length>0) {
                    double low=rates.Min(t=>t.Seconds(0,row.SpatialQueries,row.Complete?row.GeometryPairs:0));
                    double high=rates.Max(t=>t.Seconds(0,row.SpatialQueries,row.Complete?row.GeometryPairs:row.BoundingBoxHigh));
                    row.DurationLowSeconds=low*.5;row.DurationHighSeconds=high*2;
                }
            }
            var warnings=new List<string>();
            if(ScanStaging.IsStaged(plan)) {
                foreach(var row in rows){row.StagedUpperBound=true;row.PossibleRelationships=checked(row.PossibleRelationships*2);row.BoundingBoxLow=0;row.BoundingBoxHigh*=2;row.BoundingBoxPairs*=2;if(row.DurationHighSeconds.HasValue)row.DurationHighSeconds*=2;}
                warnings.Add("Detailed runs hard clashes first, then clearance only for elements touching confirmed hard-clash areas. The two-pass estimate is a conservative upper bound; no confirmed areas means clearance is skipped. Each completed pass is saved separately.");
            }
            if(comparable.Length==0)warnings.Add("Duration is unavailable until a comparable completed scan supplies measured timings.");
            else warnings.Add("Duration has low confidence: historical rates do not predict solid complexity or Boolean failures.");
            if(comparable.Length>0&&rows.Any(p=>!p.DurationLowSeconds.HasValue))warnings.Add("Some relationships lack comparable geometry timings; total duration is unavailable.");
            if(rows.Any(p=>p.Capped||!p.Complete))warnings.Add("Bounding-box sampling is bounded. Candidate ranges can be broad in dense or unsampled areas.");
            if(plan.LinkToLink)warnings.Add($"Link-to-link will evaluate up to {rows.Sum(p=>(double)p.PossibleRelationships):N0} source relationships. Only the selected link pairs are included.");
            var result=new ScanEstimate {PlanFingerprint=ScanProfiles.Fingerprint(plan),Models=Array.AsReadOnly(models.ToArray()),Pairs=rows.OrderByDescending(p=>p.PossibleRelationships).ToList().AsReadOnly(),
                DisciplinePairs=allowedDisciplines.OrderBy(s=>s,StringComparer.Ordinal).ToList().AsReadOnly(),SourceElements=uniqueSources.Count,CalibrationScans=comparable.Length,Warnings=warnings.AsReadOnly()};
            if(rows.Count>0&&rows.All(p=>p.DurationLowSeconds.HasValue)) {
                result.DurationLowSeconds=rows.Sum(p=>p.DurationLowSeconds!.Value)+comparable.Min(t=>t.Seconds(indexedCount,0,0))*.5;
                result.DurationHighSeconds=rows.Sum(p=>p.DurationHighSeconds!.Value)+comparable.Max(t=>t.Seconds(indexedCount,0,0))*2;
                if(ScanStaging.IsStaged(plan))result.DurationHighSeconds+=comparable.Max(t=>t.Seconds(indexedCount,0,0))*2;
            }
            job.Result=result;
        }
        private static double Distance(AABB a,AABB b)
        {
            double x=Math.Max(0,Math.Max(a.MinX-b.MaxX,b.MinX-a.MaxX)),y=Math.Max(0,Math.Max(a.MinY-b.MaxY,b.MinY-a.MaxY)),z=Math.Max(0,Math.Max(a.MinZ-b.MaxZ,b.MinZ-a.MaxZ));return Math.Sqrt(x*x+y*y+z*z);
        }
    }
}
