using BIMEngine.Core.Spatial;
using ClashResolveAI;
using ClashResolveAI.Core;
using ClashResolveAI.Rules;
using Newtonsoft.Json;

internal static class Phase56Checks
{
    public static void Run(Action<bool,string> check)
    {
        void Reject(Action action,string label){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}check(rejected,label);}
        var ruleSet=new RuleSet {Rules=new(){{"Generic",new ClashRule {ClearanceMM=50}},{"Duct_vs_Beam",new ClashRule {ClearanceMM=100}}}};
        ScanPlan Basis(ScanMode mode=ScanMode.HardOnly,IEnumerable<ScanModelPair>? excluded=null,IEnumerable<Discipline>? disciplines=null,IEnumerable<string>? levels=null,
            IEnumerable<string>? clearance=null)=>new("estimate-project",ScanProfile.Advanced,mode==ScanMode.HardOnly?ScanStage.HardClash:ScanStage.Coordination,mode,
                true,true,false,false,new[]{"a","b"},levelIds:levels,ruleSetContent:JsonConvert.SerializeObject(ruleSet),clearanceRuleKeys:clearance,
                candidatePolicy:new(includeGenericModels:true,includeInsulation:true),disciplines:disciplines,excludedRelationships:excluded);
        var floors=new[]{new ScanFloor("lower","Level 01",0),new ScanFloor("upper","Level 02",10)};
        ScanProfileSelection Selection(ScanProfile p=ScanProfile.QuickCoordination)=>new(){Profile=p,Links=new(){"a","b"},LevelIds=new(){"lower"}};
        var quickChoice=Selection();quickChoice.InsideLinks=true;quickChoice.Mode=ScanMode.HardAndClearance;quickChoice.IncludeGenericModels=true;
        var quick=ScanProfiles.Create(Basis(),quickChoice);
        check(quick.Profile==ScanProfile.QuickCoordination&&quick.Mode==ScanMode.HardOnly&&quick.Stage==ScanStage.HardClash,"Quick enforces a hard-clash first pass");
        check(quick.HostToHost&&quick.HostToLinks&&!quick.InsideLinks&&!quick.LinkToLink&&quick.Relationships().Count==3,"Quick schedules host and selected links without internal or Cartesian link pairs");
        check(!quick.CandidatePolicy.IncludeGenericModels&&!quick.CandidatePolicy.IncludeInsulation&&quick.CandidatePolicy.ExcludeNamedSupports&&quick.CandidatePolicy.IncludeStructural,"Quick enforces coordination candidate defaults");
        var unbounded=Selection();unbounded.LevelIds.Clear();Reject(()=>ScanProfiles.Create(Basis(),unbounded),"Quick requires a floor or scoped zone");
        unbounded.LevelIds.AddRange(new[]{"lower","upper"});Reject(()=>ScanProfiles.Create(Basis(),unbounded),"Quick rejects multiple floors");
        unbounded.LevelIds.Clear();unbounded.Zones.Add(new ScanZone(new CoordinationZone {Name="Unbounded",LinkedModel="a.rvt"}));
        Reject(()=>ScanProfiles.Create(Basis(),unbounded),"A link-only zone cannot bypass Quick's area restriction");
        unbounded.Zones.Clear();unbounded.Zones.Add(new ScanZone(new CoordinationZone {Name="MEP",WorksetName="MEP"}));
        check(ScanProfiles.Create(Basis(),unbounded).Zones.Count==1,"Quick accepts a bounded workset zone");
        var detailedChoice=Selection(ScanProfile.DetailedCoordination);
        Reject(()=>ScanProfiles.Create(Basis(),detailedChoice),"Detailed requires explicit clearance selections");
        detailedChoice.ClearanceRules.Add("Duct_vs_Beam");detailedChoice.InsideLinks=true;detailedChoice.LevelIds.Add("upper");
        var detailed=ScanProfiles.Create(Basis(),detailedChoice);
        check(detailed.Mode==ScanMode.HardAndClearance&&detailed.InsideLinks&&!detailed.LinkToLink&&detailed.LevelIds.Count==2,"Detailed combines selected clearance with optional internal links and multiple floors");
        var advancedChoice=Selection(ScanProfile.Advanced);advancedChoice.LevelIds.Clear();advancedChoice.Stage=ScanStage.LinkToLink;
        Reject(()=>ScanProfiles.Create(Basis(),advancedChoice),"Advanced link-to-link requires an explicit pair");
        advancedChoice.LinkPairs.Add(new ScanModelPair("a","b"));var cross=ScanProfiles.Create(Basis(),advancedChoice);
        check(cross.Relationships().Count==1&&cross.LinkToLink&&!cross.HostToHost&&!cross.HostToLinks&&!cross.InsideLinks,"Cross-link is an explicit separate job");
        advancedChoice.Stage=ScanStage.InsulationClearance;advancedChoice.ClearanceRules.Add("Duct_vs_Beam");var insulation=ScanProfiles.Create(Basis(),advancedChoice);
        check(insulation.Mode==ScanMode.HardAndClearance&&insulation.CandidatePolicy.IncludeInsulation&&!insulation.LinkToLink,"Insulation runs in a dedicated clearance pass");
        advancedChoice.ClearanceRules.Clear();Reject(()=>ScanProfiles.Create(Basis(),advancedChoice),"An empty Advanced clearance choice cannot silently enable every rule");
        advancedChoice.Stage=ScanStage.Coordination;advancedChoice.RestrictDisciplines=true;
        Reject(()=>ScanProfiles.Create(Basis(),advancedChoice),"Empty Advanced discipline selection cannot widen scope");
        advancedChoice.RestrictDisciplines=false;advancedChoice.RestrictCategories=true;
        Reject(()=>ScanProfiles.Create(Basis(),advancedChoice),"Empty Advanced category selection cannot widen scope");
        var removed=new ScanModelPair("","a");var restricted=Basis(excluded:new[]{removed});
        check(restricted.Relationships().Count==2&&!restricted.Relationships().Any(p=>p.Matches("","a")),"Removing an expensive relationship changes executable scope");
        check(ScanProfiles.Fingerprint(restricted)!=ScanProfiles.Fingerprint(Basis()),"Relationship removals invalidate the estimate fingerprint");
        check(ScanProfiles.Fingerprint(JsonConvert.DeserializeObject<ScanPlan>(JsonConvert.SerializeObject(restricted))!)==ScanProfiles.Fingerprint(restricted),"Reviewed plan fingerprint survives persistence");
        ElementClassification Classified(Discipline d,long category=10)=>new(d,category,d.ToString(),"","","",true,"",.95,ClassificationEvidence.RevitCategory,"",kind:CoordinationElementKind.Mep);
        var hvac=Classified(Discipline.HVAC);var structural=Classified(Discipline.Structural,20);var medical=Classified(Discipline.MedicalGas,30);var electrical=Classified(Discipline.Electrical,40);
        var boundedPolicy=new CompiledPairPolicy(Basis(disciplines:new[]{Discipline.HVAC,Discipline.Structural}));
        check(!boundedPolicy.Resolve(new ClassifiedPartition(medical),new ClassifiedPartition(electrical)).Allowed,"Advanced discipline subset cannot enable an unsupported pair");
        check(!ScanPlanEligibility.Includes(Basis(disciplines:new[]{Discipline.HVAC}),structural),"Discipline restrictions apply before estimate/index collection");
        AABB Box(double x=0,double z=1)=>new(x,0,z,x+2,2,z+2);
        var models=new[]{new EstimateModel("","Host","",true,100),new EstimateModel("a","Mechanical.rvt","Mechanical.rvt",true,100),new EstimateModel("b","Structural.rvt","Structural.rvt",true,100)};
        var elements=new[]{new EstimateElement("",1,hvac,Box()),new EstimateElement("",2,hvac,Box(.5)),new EstimateElement("",3,structural,Box(.5)),
            new EstimateElement("a",1,hvac,Box()),new EstimateElement("b",1,structural,Box())};
        ScanEstimate Estimate(ScanPlan plan,IReadOnlyList<EstimateElement> entries,IEnumerable<ScanTimingObservation>? timings=null,int budget=512,int visits=100000)
        {using var job=ScanEstimateCalculator.Create(plan,models,entries,floors,timings,budget,visits);while(!job.Advance(10)){}return job.Result!;}
        var estimate=Estimate(Basis(),elements);var host=estimate.Pairs.Single(p=>p.Pair.Source==""&&p.Pair.Target=="");
        check(host.Complete&&host.BoundingBoxPairs==3&&host.GeometryPairs==3,"Complete box estimate counts unique in-model candidates once");
        check(host.PossibleRelationships==6,"Possible source relationships retain their directed meaning");
        check(estimate.Pairs.All(p=>p.Complete)&&estimate.BoundingBoxPairs==8,"Exact small estimates include each selected host-link relationship");
        check(estimate.DurationLowSeconds==null&&estimate.Duration.Contains("Unavailable"),"No fabricated duration is shown without measured timings");
        check(estimate.SourceElements==3&&estimate.DisciplinePairs.Contains("HVAC ↔ Structural"),"Estimate exposes scoped source counts and candidate discipline pairs");
        var trimmed=Estimate(restricted,elements);
        check(trimmed.Pairs.Count==2&&trimmed.PossibleRelationships<estimate.PossibleRelationships,"Excluded relationships disappear from workload totals");
        var timing=new ScanTimingObservation(Basis(),5,20,8,100,200,800,2200,1100);
        var calibrated=Estimate(Basis(),elements,new[]{timing});
        check(calibrated.DurationLowSeconds>0&&calibrated.DurationHighSeconds>calibrated.DurationLowSeconds&&calibrated.CalibrationScans==1,"Measured comparable rates produce an explicit duration range");
        var otherMode=Estimate(Basis(ScanMode.HardAndClearance),elements,new[]{timing});
        check(otherMode.DurationLowSeconds==null&&otherMode.CalibrationScans==0,"Hard-only rates cannot silently calibrate clearance work");
        var noGeometryRate=new ScanTimingObservation(Basis(),5,20,0,100,200,0,600,300);
        check(Estimate(Basis(),elements,new[]{noGeometryRate}).DurationLowSeconds==null,"Timing records without geometry work cannot estimate a geometry workload");
        var sampled=Estimate(Basis(),elements,budget:1);
        check(sampled.Pairs.Any(p=>!p.Complete)&&sampled.Pairs.All(p=>p.BoundingBoxHigh>=p.BoundingBoxLow),"Limited sampling exposes incomplete counts and ordered ranges");
        using(var dense=ScanEstimateCalculator.Create(Basis(),models,elements,floors,sourceBudget:5,candidateBudget:2)) {
            while(!dense.Advance(10)){}check(dense.CandidateVisits<=2&&dense.Result!.Pairs.Any(p=>p.Capped),"Dense-model candidate visits respect the global budget");
            check(dense.Result!.Pairs.Where(p=>p.Capped).All(p=>p.BoundingBoxHigh==p.PossibleRelationships),"Capped rows retain conservative potential upper bounds");
        }
        using(var cancel=ScanEstimateCalculator.Create(Basis(),models,elements,floors)){cancel.Dispose();check(cancel.Cancelled&&cancel.Advance()&&cancel.Result==null,"Cancelling a metadata estimate cannot produce an approved result");}
        var boundary=new[]{new EstimateElement("",1,hvac,Box(z:9)),new EstimateElement("b",2,structural,Box(z:11.25))};
        var boundaryEstimate=Estimate(Basis(ScanMode.HardAndClearance,levels:new[]{"lower"},clearance:new[]{"Duct_vs_Beam"}),boundary);
        check(boundaryEstimate.Pairs.Single(p=>p.Pair.Target=="b").GeometryPairs==1,"Boundary-crossing floor source retains clearance targets above the floor");
        var hardBoundary=Estimate(Basis(levels:new[]{"lower"}),boundary);
        check(hardBoundary.BoundingBoxPairs==0,"Hard-only estimate does not use the clearance search radius");
        var distant=new[]{new EstimateElement("",1,hvac,Box()),new EstimateElement("a",2,hvac,Box(2.25)),new EstimateElement("b",2,structural,Box(2.25))};
        var subsetEstimate=Estimate(Basis(ScanMode.HardAndClearance,clearance:new[]{"Duct_vs_Beam"}),distant);
        check(subsetEstimate.Pairs.Single(p=>p.Pair.Target=="a").GeometryPairs==0&&subsetEstimate.Pairs.Single(p=>p.Pair.Target=="b").GeometryPairs==1,"Only selected rules expand the clearance search");
        var emptyScope=Estimate(Basis(levels:new[]{"upper"}),elements);
        check(emptyScope.SourceElements==0&&emptyScope.PossibleRelationships==0&&emptyScope.BoundingBoxPairs==0,"Empty floor scope does not fall back to an entire-model estimate");
        Reject(()=>Estimate(Basis(levels:new[]{"deleted"}),elements),"Deleted floor selection fails explicitly");
        var many=new List<EstimateElement>();for(int i=0;i<50000;i++){many.Add(new EstimateElement("",i,hvac,Box()));many.Add(new EstimateElement("a",i,hvac,Box()));}
        var wide=Estimate(Basis(excluded:new[]{new ScanModelPair("",""),new ScanModelPair("","b")}),many,budget:1,visits:8);
        check(wide.PossibleRelationships==2500000000L,"Potential relationship arithmetic remains correct above Int32 capacity");
        var oldSettings=new AppSettings {ScanWithinLinks=false,ScanLinkedModels=false,IncludeGenericModels=true,IncludeInsulation=true,ExcludeNamedSupports=false,
            RuleSetName="LiveRules",MinimumOverlapMM3=7,InsulationMM=42,MaintenanceMM=99,IncludeStructural=false,ExcludeConnectedJoints=false};
        string Live(AppSettings s)=>JsonConvert.SerializeObject(new{s.ScanWithinLinks,s.ScanLinkedModels,s.IncludeGenericModels,s.IncludeInsulation,s.ExcludeNamedSupports,s.RuleSetName,s.MinimumOverlapMM3,s.InsulationMM,s.MaintenanceMM,s.IncludeStructural,s.ExcludeConnectedJoints,s.LiveMode});
        var before=Live(oldSettings);oldSettings.MigrateScanConfiguration();
        check(before==Live(oldSettings),"Configuration migration preserves every existing Live Monitor scan preference");
        var full=oldSettings.FullScanSnapshot();full.RuleSetName="FullOnly";full.MinimumOverlapMM3=99;
        check(oldSettings.RuleSetName=="LiveRules"&&oldSettings.MinimumOverlapMM3==7,"Full Scan snapshot is detached from Live Monitor preferences");
        check(oldSettings.FullScanRuleSetName=="LiveRules"&&oldSettings.FullScanMinimumOverlapMM3==7,"Existing shared preferences migrate once into separate Full Scan fields");
        oldSettings.FullScanRuleSetName="CoordinatorRules";oldSettings.MigrateScanConfiguration();
        check(oldSettings.FullScanRuleSetName=="CoordinatorRules"&&before==Live(oldSettings),"Repeated migration retains separate coordinator choices without changing Live Monitor");
        var streamed=new Octree(Box().Expand(10));foreach(var e in elements)streamed.Insert(new SpatialEntry(e.ElementId,e.Bounds));
        check(streamed.Query(Box()).Count==streamed.Enumerate(Box()).Count(),"Streaming estimate traversal agrees with the existing spatial query");
    }
}
