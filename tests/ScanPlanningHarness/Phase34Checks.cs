using ClashResolveAI.Core;
using ClashResolveAI.Rules;
using Newtonsoft.Json;

internal static class Phase34Checks
{
    public static void Run(Action<bool,string> check,string folder)
    {
        void Reject(Action action,string label){bool failed=false;try{action();}catch(Exception){failed=true;}check(failed,label);}
        ScanPlan Plan(RuleSet? rules=null,IEnumerable<ScanDisciplinePair>? pairs=null,IEnumerable<long>? cats=null,IEnumerable<string>? clearance=null,
            IEnumerable<ScanDisciplineRule>? matrix=null,ScanStage stage=ScanStage.Coordination,ScanMode mode=ScanMode.HardAndClearance) =>
            new("project",ScanProfile.Advanced,stage,mode,true,false,false,false,Array.Empty<string>(),disciplinePairs:pairs,categoryIds:cats,clearanceRuleKeys:clearance,
                ruleSetContent:JsonConvert.SerializeObject(rules??new RuleSet {Rules=new(){{"Generic",new ClashRule {ClearanceMM=50}}}}),disciplineRules:matrix,
                candidatePolicy:new(includeGenericModels:true),medicalGasKeywords:new[]{"MEDICAL OUTLET"});
        ClassifiedPartition Part(Discipline d,long id,string category="",string alias="",CoordinationElementKind kind=CoordinationElementKind.Mep)=>
            new(new ElementClassification(d,id,category,"","","",true,"",.95,ClassificationEvidence.RevitCategory,"",categoryAlias:alias,kind:kind));
        var hvac=Part(Discipline.HVAC,10,"Ducts","Duct");var structure=Part(Discipline.Structural,20,"Structural Framing","Beam");
        var medical=Part(Discipline.MedicalGas,30);var electrical=Part(Discipline.Electrical,40);
        var basic=new CompiledPairPolicy(Plan());
        check(basic.Resolve(hvac,structure).Allowed,"Default coordination matrix permits HVAC against structure");
        check(!basic.Resolve(medical,electrical).Allowed,"Unsupported medical/electrical pair is rejected before spatial retrieval");
        check(!basic.Resolve(structure,structure).Allowed,"Generic fallback cannot silently enable structural self-coordination");
        check(!basic.Resolve(Part(Discipline.Unknown,50),hvac).Allowed,"Unknown discipline never enters spatial queries");
        var cached=basic.Resolve(hvac,structure);int compiled=basic.CompiledPairCount;
        check(ReferenceEquals(cached,basic.Resolve(hvac,structure))&&compiled==basic.CompiledPairCount,"Rule resolution is cached by metadata partitions");
        var detached=cached.CopyRule();detached.ClearanceMM=9999;
        check(cached.CopyRule().ClearanceMM==50,"Callers cannot mutate a compiled rule through a returned rule copy");
        var selected=new CompiledPairPolicy(Plan(pairs:new[]{new ScanDisciplinePair(Discipline.MedicalGas,Discipline.Electrical)}));
        check(selected.Resolve(medical,electrical).Allowed&&!selected.Resolve(hvac,structure).Allowed,"Explicit discipline selection admits only the requested pair");
        var categories=new CompiledPairPolicy(Plan(cats:new long[]{10,20}));
        check(categories.Resolve(hvac,structure).Allowed&&!categories.Resolve(hvac,electrical).Allowed,"Category selections constrain both endpoints");
        var rules=new RuleSet {Rules=new(){{"Generic",new ClashRule {ClearanceMM=11}},{"Duct_vs_Beam",new ClashRule {ClearanceMM=22}}},
            DisciplinePairs=new(){{"HVAC|Structural",new ClashRule {ClearanceMM=33}}},CategoryPairs=new(){{"10|20",new ClashRule {ClearanceMM=44,Severity="Critical"}}}};
        var precedence=new CompiledPairPolicy(Plan(rules));
        check(precedence.Resolve(hvac,structure).ClearanceMM==44&&precedence.Resolve(hvac,structure).RuleId=="CatPair:10|20","Numeric category rule takes precedence over named and discipline rules");
        check(precedence.Resolve(structure,hvac).ClearanceMM==44,"Category rules work in reverse endpoint order");
        rules.CategoryPairs.Clear();rules.CategoryPairs["Duct|Beam"]=new ClashRule {ClearanceMM=55};
        check(new CompiledPairPolicy(Plan(rules)).Resolve(hvac,structure).ClearanceMM==55,"Legacy friendly category aliases remain supported before spatial queries");
        rules.CategoryPairs.Clear();
        check(new CompiledPairPolicy(Plan(rules)).Resolve(hvac,structure).ClearanceMM==22,"Named matrix rule retains precedence over discipline-pair rule");
        rules.Rules.Remove("Duct_vs_Beam");
        check(new CompiledPairPolicy(Plan(rules)).Resolve(hvac,structure).ClearanceMM==33,"Discipline rule is resolved when a named rule is unavailable");
        rules.CategoryPairs["30|40"]=new ClashRule {ClearanceMM=66};
        check(new CompiledPairPolicy(Plan(rules)).Resolve(medical,electrical).Allowed,"Explicit positive category rule can enable an otherwise unsupported discipline pair");
        check(!new CompiledPairPolicy(Plan(rules,pairs:new[]{new ScanDisciplinePair(Discipline.HVAC,Discipline.Structural)})).Resolve(medical,electrical).Allowed,"Category override cannot escape the user's explicit discipline restriction");
        rules.CategoryPairs["10|20"]=new ClashRule {Ignore=true};
        check(!new CompiledPairPolicy(Plan(rules)).Resolve(hvac,structure).Allowed,"Ignored category rule rejects a supported group before querying its index");
        rules.CategoryPairs["10|20"]=new ClashRule {Severity="Ignore"};
        check(!new CompiledPairPolicy(Plan(rules)).Resolve(hvac,structure).Allowed,"Severity Ignore is honored without relying on the boolean ignore flag");
        var subsetRules=new RuleSet {Rules=new(){{"Generic",new ClashRule {ClearanceMM=50}},{"Duct_vs_Beam",new ClashRule {ClearanceMM=100}}}};
        var subset=new CompiledPairPolicy(Plan(subsetRules,clearance:new[]{"Duct_vs_Beam"}));
        check(subset.Resolve(hvac,structure).ClearanceEnabled,"Selected clearance rule enables pair-specific clearance");
        var sameHvac=subset.Resolve(hvac,hvac);
        check(sameHvac.Allowed&&!sameHvac.ClearanceEnabled,"Unselected clearance rule preserves hard-clash testing in a combined scan");
        check(!new CompiledPairPolicy(Plan(subsetRules,clearance:new[]{"Duct_vs_Beam"},stage:ScanStage.Clearance)).Resolve(hvac,hvac).Allowed,"Clearance-only stage skips unselected rules entirely");
        var reciprocalRules=new RuleSet {DisciplinePairs=new(){{"MedicalGas|Electrical",new ClashRule {ClearanceMM=100}},{"Electrical|MedicalGas",new ClashRule {ClearanceMM=100}}}};
        var reciprocal=new CompiledPairPolicy(Plan(reciprocalRules,clearance:new[]{"DiscPair:MedicalGas|Electrical"}));
        check(reciprocal.Resolve(medical,electrical).ClearanceEnabled&&reciprocal.Resolve(electrical,medical).ClearanceEnabled,"Selecting a reciprocal discipline rule enables clearance in either endpoint order");
        Reject(()=>new CompiledPairPolicy(Plan(clearance:new[]{"missing-rule"})),"Unknown clearance-rule selection is rejected instead of widening scope");
        var insulation=new CompiledPairPolicy(Plan(stage:ScanStage.InsulationClearance));
        check(!insulation.Resolve(hvac,structure).Allowed,"Dedicated insulation stage rejects pairs without insulation endpoints");
        check(insulation.Resolve(Part(Discipline.HVAC,60,kind:CoordinationElementKind.Insulation),structure).Allowed,"Dedicated insulation stage can query valid insulation/structure groups");
        var matrix=new List<ScanDisciplineRule>{new(Discipline.HVAC,Discipline.Structural)};var frozen=Plan(matrix:matrix);matrix.Clear();
        check(new CompiledPairPolicy(frozen).Resolve(hvac,structure).Allowed,"Changing a caller's matrix list cannot change a frozen scan plan");
        check(new CompiledPairPolicy(JsonConvert.DeserializeObject<ScanPlan>(JsonConvert.SerializeObject(frozen))!).Resolve(hvac,structure).Allowed,"Serialized plans preserve the compiled discipline policy inputs");

        var scope=new ScanScope("project");scope.RestrictToEligibleElements();scope.RestrictToPartitions();
        foreach(long id in new long[]{1,2,3})scope.NoteEligible(id,"");
        scope.NotePartition(1,"",hvac.Key);scope.NotePartition(2,"",structure.Key);scope.NotePartition(3,"",medical.Key);scope.Note(1,"","");
        scope.AllowTargetPartition("","",hvac.Key,structure.Key,clearance:false);
        check(scope.ContainsPair("project",1,"",2,"","hard"),"Partition coverage records reliable absence for an allowed hard pair");
        check(!scope.ContainsPair("project",1,"",3,"","pruned"),"Pruned discipline groups cannot be marked resolved through source coverage");
        var prior=new ClashResult {HostDocumentKey="project",ElementAId=1,ElementBId=2,TestType=ClashTestType.ClearanceClash,Status=ClashStatus.Active};
        check(!scope.Contains(prior),"Hard-only evaluation for a selected pair cannot resolve its old clearance finding");
        ResultLifecycle.Merge(new[]{prior},new(),ScanMode.HardAndClearance,scope.Contains);
        check(prior.Status==ClashStatus.Active,"Combined-scan merge retains findings from unselected clearance rules");
        scope.AllowTargetPartition("","",hvac.Key,structure.Key,clearance:true);
        check(scope.Contains(prior),"Explicit clearance coverage permits comparable clearance absence evaluation");

        Directory.CreateDirectory(folder);var store=new ProjectClassificationStore(Path.Combine(folder,"projects"));
        var empty=store.Load("project");check(empty.Mappings.Count==0&&!File.Exists(store.PathFor("project")),"Loading a new project mapping snapshot is read-only");
        var outlet=new ClassificationMapping("outlet",Discipline.MedicalGas,"MEDICAL OUTLET",2);
        var saved=store.Save("project",new[]{outlet},empty.Fingerprint);
        check(new ProjectClassificationStore(Path.Combine(folder,"projects")).Load("project").Mappings[0].Discipline==Discipline.MedicalGas,"Project mappings survive a new store instance");
        check(store.Load("other-project").Mappings.Count==0,"Mappings are isolated by project identity");
        check(store.PathFor("../../escape").StartsWith(Path.GetFullPath(Path.Combine(folder,"projects"))+Path.DirectorySeparatorChar),"Project identities cannot escape the configured storage directory");
        Reject(()=>store.Save("project",Array.Empty<ClassificationMapping>(),empty.Fingerprint),"Stale editor revision cannot overwrite newer project mappings");
        check(store.Load("project").Mappings.Count==1,"Rejected concurrent save preserves the newer mappings");
        var changed=store.Save("project",new[]{new ClassificationMapping("outlet",Discipline.HVAC,"MEDICAL OUTLET",2)},saved.Fingerprint);
        check(changed.Fingerprint!=saved.Fingerprint&&File.Exists(store.PathFor("project")+".bak"),"Atomic updates change the content fingerprint and retain the previous file");
        Reject(()=>store.Save("project",new[]{outlet,outlet},changed.Fingerprint),"Duplicate mapping IDs cannot be persisted");
        Reject(()=>store.Save("project",new[]{new ClassificationMapping("unknown",Discipline.Unknown,"X")},changed.Fingerprint),"Unknown classification requires an explicit exclusion when saving");
        var portable=Path.Combine(folder,"portable.json");store.Export(portable,"project",new[]{outlet});
        var imported=store.ReadImport(portable);check(imported.Count==1&&store.Load("other-project").Mappings.Count==0,"Import preview does not mutate any project");
        store.Save("other-project",imported,store.Load("other-project").Fingerprint);
        check(store.Load("other-project").Mappings.Count==1,"Explicit import can reuse mappings in a different project");
        File.WriteAllText(store.PathFor("other-project"),File.ReadAllText(portable));
        Reject(()=>store.Load("other-project"),"Copying another project's raw storage file cannot silently change project ownership");
        var malformed=Path.Combine(folder,"malformed.json");File.WriteAllText(malformed,"not-json");
        Reject(()=>store.ReadImport(malformed),"Malformed imports are rejected before changing a draft");
        File.WriteAllText(malformed,"{\"Version\":99,\"DocumentKey\":\"project\",\"Mappings\":[]}");
        Reject(()=>store.ReadImport(malformed),"Future mapping schemas are rejected explicitly");

        var reviewStore=new ProjectClassificationStore(Path.Combine(folder,"review-projects"));var model=new ClassificationReviewModel(Plan(),reviewStore);
        var input=new ClassificationInput {CategoryId=2,Category="Generic Model",Kind=CoordinationElementKind.GenericModel,FamilyType="MEDICAL OUTLET"};
        var sample=new ClassificationReviewSample(input,"Mechanical.rvt");model.AddSample(sample);model.AddSample(new(input,"Mechanical.rvt",2));model.Refresh();
        var row=model.Rows.Single();check(row.Count==3&&row.CurrentClassification=="Unknown"&&row.SuggestedClassification.Contains("MedicalGas"),"Review shows current Unknown separately from a visible medical-gas suggestion");
        input.FamilyType="changed";sample.Input.FamilyType="changed again";
        check(row.FamilyType=="MEDICAL OUTLET","Review rows retain copied inputs when caller metadata changes");
        var confirmation=model.Confirm(row);
        check(row.Classification.Discipline==Discipline.MedicalGas&&row.Classification.Evidence==ClassificationEvidence.ProjectMapping&&model.Dirty,"Confirm creates a visible explicit draft mapping with provenance");
        check(reviewStore.Load("project").Mappings.Count==0,"Unsaved review edits do not alter persisted project mappings");
        check(confirmation.MatchMode==FamilyTypeMatch.Exact&&confirmation.SystemTypeEquals==""&&confirmation.SystemClassificationEquals=="","Confirmation is scoped to exact family and system context, including unassigned systems");
        check(!confirmation.Matches(new ClassificationInput {CategoryId=2,FamilyType="MEDICAL OUTLET LARGE"}),"Confirmation does not accidentally match another longer family/type name");
        check(!confirmation.Matches(new ClassificationInput {CategoryId=2,FamilyType="MEDICAL OUTLET",SystemType="different"}),"Confirmation does not override another system using the same family/type");
        var firstConfirm=JsonConvert.SerializeObject(confirmation);model.Confirm(row);
        check(JsonConvert.SerializeObject(model.Mappings.Single())==firstConfirm,"Repeating the same confirmation is idempotent");
        model.Save();check(!model.Dirty&&reviewStore.Load("project").Mappings.Count==1,"Save applies the reviewed draft and advances the saved revision");
        model.Keep(row);check(!model.Dirty&&row.ReviewStatus.Contains("Kept"),"Keep records review without creating unnecessary override mappings");
        model.Confirm(row,exclude:true);check(!row.Classification.IsCoordinationCandidate&&model.Dirty,"Exclude previews its reason in the classification table");
        model.Reload();check(!model.Dirty&&row.Classification.IsCoordinationCandidate,"Reload restores saved classifications and discards only the draft");
        var reviewExport=Path.Combine(folder,"review-export.json");model.ExportDraft(reviewExport);model.Remove(model.Mappings.Single());model.ImportDraft(reviewExport);
        check(model.Mappings.Count==1&&row.Classification.Discipline==Discipline.MedicalGas,"Export/import round-trip retains the reviewed exact mapping conditions");
        var otherEditor=new ClassificationReviewModel(Plan(),reviewStore);model.Confirm(row,Discipline.HVAC);model.Save();
        otherEditor.Upsert(new ClassificationMapping("other",Discipline.Electrical,"DEVICE"));
        Reject(()=>otherEditor.Save(),"A second review window cannot overwrite a newer saved mapping revision");
    }
}
