using ClashResolveAI;
using ClashResolveAI.Core;
using Newtonsoft.Json;

int passed=0;
void Check(bool condition,string name) {if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
void Reject(Action action,string name) {bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Check(rejected,name);}
ScanPlan Plan(bool host=true,bool hostLinks=true,bool inside=false,bool cross=false,IEnumerable<ScanModelPair>? pairs=null,
    IEnumerable<string>? links=null,IEnumerable<ScanZone>? zones=null,IEnumerable<ClassificationMapping>? mappings=null) =>
    new("project",ScanProfile.Advanced,ScanStage.HardClash,ScanMode.HardOnly,host,hostLinks,inside,cross,
        links ?? new[]{"structural-instance","mechanical-instance","plumbing-instance"},pairs,zones:zones,mappings:mappings);
var basic=Plan();
Check(basic.Relationships().Count==4,"Default relationships contain host-host plus three host-link pairs");
Check(basic.Relationships().All(p=>p.Source==""),"Default plan never scans linked sources");
var enabledOnly=Plan(cross:true);
Check(enabledOnly.Relationships().Count==basic.Relationships().Count,"Link-to-link switch alone never generates a cross product");
var explicitPair=new ScanModelPair("structural-instance","mechanical-instance");
var pairOnly=Plan(host:false,hostLinks:false,cross:true,pairs:new[]{explicitPair});
Check(pairOnly.Relationships().Count==1 && pairOnly.Relationships()[0].Matches(explicitPair.Source,explicitPair.Target),"Pair-only job schedules exactly the selected relationship");
Check(!pairOnly.Relationships().Any(p=>p.Matches("mechanical-instance","plumbing-instance")),"Unselected MEP pair is absent");
var internalOnly=Plan(host:false,hostLinks:false,inside:true);
Check(internalOnly.Relationships().Count==3 && internalOnly.Relationships().All(p=>p.Source==p.Target && p.Source!=""),"Inside-link scanning works with link-to-link off and host scanning off");
var repeated=Plan(host:false,hostLinks:false,cross:true,links:new[]{"same-file-instance-1","same-file-instance-2"},pairs:new[]{new ScanModelPair("same-file-instance-1","same-file-instance-2")});
Check(repeated.Relationships().Count==1,"Repeated instances of the same file remain distinct endpoints");
Reject(()=>Plan(pairs:new[]{new ScanModelPair("host","missing")}),"Unknown pair endpoints are rejected");
Reject(()=>Plan(pairs:new[]{new ScanModelPair("structural-instance","structural-instance")}),"Internal-link relationship cannot be smuggled into cross-link pairs");
Reject(()=>Plan(pairs:new[]{new ScanModelPair("","mechanical-instance")}),"Host-link relationship cannot be smuggled into cross-link pairs");
Check(Plan(cross:true,pairs:new[]{explicitPair,new ScanModelPair(explicitPair.Target,explicitPair.Source)}).LinkPairs.Count==1,"Reverse pair selections are deduplicated");
var mutableLinks=new List<string>{"instance-a"};var frozen=Plan(links:mutableLinks);
mutableLinks.Add("instance-b");
Check(frozen.SelectedLinkInstanceIds.Count==1,"Caller link-list changes cannot mutate a plan");
bool readOnly=false;try{((IList<string>)frozen.SelectedLinkInstanceIds)[0]="changed";}catch(NotSupportedException){readOnly=true;}
Check(readOnly,"Plan collections reject mutation through IList casts");
var zone=new CoordinationZone {Name="Zone A",LevelName="L02",WorksetName="MEP"};
var zonePlan=Plan(zones:new[]{new ScanZone(zone)});zone.LevelName="L99";
Check(zonePlan.Zones[0].LevelName=="L02","Mutable project zones are detached from scan plans");
var zoneCopy=zonePlan.Zones[0].ToZone();zoneCopy.WorksetName="changed";
Check(zonePlan.Zones[0].WorksetName=="MEP","Returning a zone copy cannot mutate the plan");
var json=JsonConvert.SerializeObject(pairOnly);
Check(json.Contains("mechanical-instance") && !json.Contains("OpenAiApiKey"),"Serialized plan contains exact pair identities and no application credentials");
var roundTrip=JsonConvert.DeserializeObject<ScanPlan>(json)!;
Check(roundTrip.Relationships().Count==1 && roundTrip.LinkPairs[0].Matches(explicitPair.Source,explicitPair.Target),"Immutable plan round-trips with its exact relationship selection");
var legacy=JsonConvert.DeserializeObject<AppSettings>("{\"IncludeLinkToLink\":true,\"ScanWithinLinks\":true,\"IncludeGenericModels\":true,\"IncludeInsulation\":true,\"ExcludeNamedSupports\":false,\"LiveMode\":1}")!;
legacy.MigrateScanConfiguration();
Check(!legacy.IncludeLinkToLink && !legacy.FullScanWithinLinks && legacy.ScanWithinLinks && !legacy.FullScanIncludeGenericModels && !legacy.FullScanIncludeInsulation && legacy.FullScanExcludeNamedSupports,"Full Scan migration preserves the legacy link setting while disabling Full Scan internal-link defaults");
Check(legacy.IncludeGenericModels && legacy.IncludeInsulation && !legacy.ExcludeNamedSupports,"Full Scan migration preserves the Live Monitor's original candidate preferences");
Check(legacy.ScanHostToHost && legacy.ScanHostToLinks && legacy.LiveMode==ScanMode.HardAndClearance,"Migration retains host coordination and the configured Live mode");
legacy.IncludeLinkToLink=true;legacy.ScanHostToHost=false;legacy.MigrateScanConfiguration();
Check(legacy.IncludeLinkToLink && !legacy.ScanHostToHost,"Migration is idempotent and preserves subsequent coordinator choices");
var defaults=new AppSettings();
Check(defaults.FullScanMode==ScanMode.HardOnly && !defaults.IncludeLinkToLink && !defaults.FullScanWithinLinks && !defaults.FullScanIncludeGenericModels && !defaults.FullScanIncludeInsulation && defaults.FullScanExcludeNamedSupports,"Fresh Full Scan settings use the approved economical defaults");

var medical=new[]{"MEDICAL","MEDICAL OUTLET","MED"};var drainage=new[]{"DRAIN","SAN"};
ElementClassification Classify(ClassificationInput input,CoordinationCandidatePolicy? policy=null,ScanStage stage=ScanStage.HardClash,IEnumerable<ClassificationMapping>? mappings=null) =>
    CoordinationClassifier.Classify(input,policy ?? new(),stage,mappings,medical,drainage);
var pipe=new ClassificationInput {CategoryId=1,Category="Pipe",Kind=CoordinationElementKind.Pipe,FamilyType="MEDICAL OUTLET",SystemType="MED",SystemClassification="Sanitary",SystemDiscipline=Discipline.GravityDrainage};
var sanitary=Classify(pipe);
Check(sanitary.Discipline==Discipline.GravityDrainage && sanitary.Evidence==ClassificationEvidence.RevitSystemClassification && sanitary.Confidence>0.9,"Revit sanitary classification wins over conflicting medical keywords");
pipe.SystemDiscipline=Discipline.Plumbing;pipe.SystemClassification="DomesticColdWater";
Check(Classify(pipe).Discipline==Discipline.Plumbing,"Authoritative domestic-water classification cannot be silently overridden by family keywords");
pipe.SystemDiscipline=Discipline.Unknown;pipe.SystemClassification="OtherPipe";
Check(Classify(pipe).Discipline==Discipline.MedicalGas && Classify(pipe).Evidence==ClassificationEvidence.SystemType,"Ambiguous pipe system falls back to system type with visible evidence");
pipe.SystemType="";
Check(Classify(pipe).Evidence==ClassificationEvidence.FamilyTypeKeyword && Classify(pipe).Confidence==0.5,"Family keywords are the last fallback with lower confidence");
pipe.FamilyType="UNASSIGNED";
Check(Classify(pipe).Discipline==Discipline.Unknown && !Classify(pipe).IsCoordinationCandidate,"Unassigned pipe service is Unknown and excluded pending review");
pipe.FamilyType="PUMP MEDIUM";
Check(Classify(pipe).Discipline==Discipline.Unknown,"Short medical keyword MED does not match MEDIUM");
var generic=new ClassificationInput {CategoryId=2,Category="Generic Model",Kind=CoordinationElementKind.GenericModel,FamilyType="Medical Outlet",SystemType="MEDICAL",SystemDiscipline=Discipline.MedicalGas};
Check(Classify(generic,new(includeGenericModels:true)).Discipline==Discipline.Unknown,"Generic models remain Unknown despite matching family/system signals");
var mapping=new ClassificationMapping("medical-outlet",Discipline.MedicalGas,"MEDICAL OUTLET");
var mapped=Classify(generic,new(includeGenericModels:true),mappings:new[]{mapping});
Check(mapped.IsCoordinationCandidate && mapped.Discipline==Discipline.MedicalGas && mapped.MappingId==mapping.Id && mapped.Confidence==1,"Explicit project mapping admits a generic model with complete provenance");
Check(!Classify(generic,mappings:new[]{mapping}).IsCoordinationCandidate,"Mapping does not bypass the profile's generic-model exclusion");
var conflict=new ClassificationMapping("conflict",Discipline.HVAC,"MEDICAL OUTLET");
Check(!Classify(generic,new(includeGenericModels:true),mappings:new[]{mapping,conflict}).IsCoordinationCandidate,"Conflicting equal-priority mappings require review");
var exclude=new ClassificationMapping("exclude-outlet",Discipline.Unknown,"MEDICAL OUTLET",exclude:true,priority:10);
Check(Classify(generic,new(includeGenericModels:true),mappings:new[]{mapping,exclude}).ExclusionReason.Contains("exclude-outlet"),"Higher-priority project exclusion wins and explains its source");
pipe.FamilyType="MEDICAL OUTLET";pipe.ConflictingSystems=true;
Check(!Classify(pipe).IsCoordinationCandidate,"Conflicting connected systems do not silently fall back to family keywords");
Check(Classify(pipe,mappings:new[]{mapping}).Discipline==Discipline.MedicalGas,"Explicit project mapping can resolve conflicting system evidence");
var equipment=new ClassificationInput {CategoryId=3,Category="Mechanical Equipment",Kind=CoordinationElementKind.Mep,CategoryDiscipline=Discipline.HVAC,FamilyType="AHU-01"};
Check(Classify(equipment).IsCoordinationCandidate && Classify(equipment).Confidence>0.9,"AHU category is a high-confidence coordination candidate");
var conduit=new ClassificationInput {Kind=CoordinationElementKind.Mep,CategoryDiscipline=Discipline.Conduit,SystemDiscipline=Discipline.Electrical};
Check(Classify(conduit).Discipline==Discipline.Conduit,"Electrical service does not erase the primary conduit category classification");
var classificationJson=JsonConvert.SerializeObject(mapped);
Check(JsonConvert.DeserializeObject<ElementClassification>(classificationJson)!.IsCoordinationCandidate,"Classification evidence and candidate status survive serialization");
foreach(string aid in new[]{"HANGER M10","Pipe_SUPPORT","SLEEVE-100","PENETRATION","ANCHOR","BRACKET","THREADED ROD","BLOCKOUT","TRAPEZE","CLAMP"}) {
    equipment.FamilyType=aid;Check(!Classify(equipment).IsCoordinationCandidate,"Construction aid excluded: "+aid);
}
equipment.FamilyType="PRODUCT WATER AHU";
Check(Classify(equipment).IsCoordinationCandidate,"Support keyword ROD does not match PRODUCT");
equipment.FamilyType="PIPE SUPPORT";
Check(Classify(equipment,new(excludeNamedSupports:false)).IsCoordinationCandidate,"Coordinator can explicitly include support families through policy");
foreach(var kind in new[]{CoordinationElementKind.Framing,CoordinationElementKind.Column}) {
    var frame=new ClassificationInput {Kind=kind,CategoryDiscipline=Discipline.Structural};
    Check(Classify(frame).IsCoordinationCandidate,"Structural "+kind+" included by default");
    Check(!Classify(frame,new(includeStructural:false)).IsCoordinationCandidate,"Structural exclusion applies to "+kind);
}
var floor=new ClassificationInput {CategoryId=10,Kind=CoordinationElementKind.Floor,CategoryDiscipline=Discipline.Structural};
Check(!Classify(floor).IsCoordinationCandidate,"Floor is excluded when selected rules do not require it");
Check(Classify(floor,new(requiredEnvelopeCategoryIds:new long[]{10})).IsCoordinationCandidate,"Floor is admitted when selected rules explicitly require it");
var insulation=new ClassificationInput {Kind=CoordinationElementKind.Insulation,CategoryDiscipline=Discipline.HVAC};
Check(!Classify(insulation,new(includeInsulation:true)).IsCoordinationCandidate,"Insulation cannot enter the hard-clash first pass even with its inclusion switch on");
Check(Classify(insulation,new(includeInsulation:true),ScanStage.InsulationClearance).IsCoordinationCandidate,"Insulation is admitted in its dedicated enabled clearance pass");
Check(!Classify(insulation,stage:ScanStage.InsulationClearance).IsCoordinationCandidate,"Dedicated stage still requires explicit insulation inclusion");
Check(!Classify(new ClassificationInput {Kind=CoordinationElementKind.Unsupported},mappings:new[]{new ClassificationMapping("unsupported",Discipline.HVAC,categoryId:0)}).IsCoordinationCandidate,"Mapping cannot admit unsupported/nonphysical categories");

var scope=new ScanScope("project");scope.RestrictToEligibleElements();scope.Note(1,"","selected-link");scope.NoteEligible(1,"");scope.NoteEligible(2,"selected-link");
Check(scope.ContainsPair("project",1,"",2,"selected-link","pair"),"Coverage includes evaluated eligible endpoints");
Check(!scope.ContainsPair("project",1,"",3,"selected-link","excluded"),"Excluded target cannot be marked resolved by source coverage");
Check(!scope.ContainsPair("project",1,"",2,"other-link","unselected"),"Coverage cannot resolve an unselected link relationship");
scope.Note(2,"selected-link","");
Check(!scope.ContainsPair("project",3,"",2,"selected-link","excluded-source"),"Reverse coverage cannot resolve an excluded host endpoint");
scope.MissingPair("failed");
Check(!scope.ContainsPair("project",1,"",2,"selected-link","failed"),"Geometry failures remain outside reliable absence coverage");
var old=new ClashResult {HostDocumentKey="project",ElementAId=1,ElementBId=3,LinkInstanceB="selected-link",TestType=ClashTestType.HardClash,Status=ClashStatus.Active};
ResultLifecycle.Merge(new[]{old},new(),ScanMode.HardOnly,scope.Contains);
Check(old.Status==ClashStatus.Active,"A newly excluded element retains its existing active finding");
Phase34Checks.Run(Check,Path.Combine(Directory.GetCurrentDirectory(),"verification","fullscan-phase34","mapping-tests-"+Guid.NewGuid().ToString("N")));
Phase56Checks.Run(Check);
Phase78Checks.Run(Check);
Console.WriteLine($"{passed} scan planning/classification/estimate/profile checks passed.");
