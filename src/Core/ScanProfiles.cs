using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Core
{
    public sealed class ScanProfileSelection
    {
        public ScanProfile Profile { get; set; }=ScanProfile.QuickCoordination;
        public ScanStage Stage { get; set; }=ScanStage.Coordination;
        public ScanMode Mode { get; set; }=ScanMode.HardOnly;
        public bool HostToHost { get; set; }=true;
        public bool HostToLinks { get; set; }=true;
        public bool InsideLinks { get; set; }
        public bool IncludeGenericModels { get; set; }
        public bool ExcludeSupports { get; set; }=true;
        public bool RestrictDisciplines { get; set; }
        public bool RestrictCategories { get; set; }
        public List<string> Links { get; set; }=new List<string>();
        public List<ScanModelPair> LinkPairs { get; set; }=new List<ScanModelPair>();
        public List<string> LevelIds { get; set; }=new List<string>();
        public List<ScanZone> Zones { get; set; }=new List<ScanZone>();
        public List<Discipline> Disciplines { get; set; }=new List<Discipline>();
        public List<long> Categories { get; set; }=new List<long>();
        public List<string> ClearanceRules { get; set; }=new List<string>();
        public List<ScanModelPair> ExcludedRelationships { get; set; }=new List<ScanModelPair>();
        public double CoordinationRadiusMM { get; set; }=2000;
    }
    public static class ScanProfiles
    {
        public static ScanPlan Create(ScanPlan basis,ScanProfileSelection selected)
        {
            bool quick=selected.Profile==ScanProfile.QuickCoordination,advanced=selected.Profile==ScanProfile.Advanced;
            bool cross=advanced&&selected.Stage==ScanStage.LinkToLink,insulation=advanced&&selected.Stage==ScanStage.InsulationClearance;
            if(advanced&&selected.RestrictCategories&&selected.Categories.Count==0)throw new ArgumentException("Select at least one category.");
            if(advanced&&selected.RestrictDisciplines&&selected.Disciplines.Count==0)throw new ArgumentException("Select at least one discipline.");
            var mode=quick?ScanMode.HardOnly:!advanced||insulation?ScanMode.HardAndClearance:selected.Mode;
            if(mode==ScanMode.HardAndClearance&&selected.ClearanceRules.Count==0)throw new ArgumentException("Select at least one clearance rule, or choose hard clashes only.");
            var plan=new ScanPlan(basis.DocumentKey,selected.Profile,cross?ScanStage.LinkToLink:insulation?ScanStage.InsulationClearance:mode==ScanMode.HardOnly?ScanStage.HardClash:ScanStage.Coordination,
                mode,!cross&&(quick||selected.HostToHost),!cross&&(quick||selected.HostToLinks),!cross&&!quick&&selected.InsideLinks,cross,
                selected.Links,cross?selected.LinkPairs:null,selected.LevelIds,selected.Zones,
                categoryIds:advanced&&selected.RestrictCategories?selected.Categories:null,clearanceRuleKeys:mode==ScanMode.HardAndClearance?selected.ClearanceRules:null,
                ruleSetName:basis.RuleSetName,ruleSetContent:basis.RuleSetContent,
                candidatePolicy:new CoordinationCandidatePolicy(!advanced||basis.CandidatePolicy.IncludeStructural,advanced&&selected.IncludeGenericModels,insulation,
                    !advanced||selected.ExcludeSupports,basis.CandidatePolicy.RequiredEnvelopeCategoryIds),mappings:basis.Mappings,
                medicalGasKeywords:basis.MedicalGasKeywords,gravityDrainageKeywords:basis.GravityDrainageKeywords,minimumOverlapMM3:basis.MinimumOverlapMM3,
                insulationMM:basis.InsulationMM,maintenanceMM:basis.MaintenanceMM,excludeConnectedJoints:!advanced||basis.ExcludeConnectedJoints,
                disciplineRules:basis.DisciplineRules,projectMappingsFingerprint:basis.ProjectMappingsFingerprint,
                disciplines:advanced&&selected.RestrictDisciplines?selected.Disciplines:null,excludedRelationships:selected.ExcludedRelationships,
                coordinationRadiusMM:selected.CoordinationRadiusMM);
            Validate(plan);return plan;
        }
        public static void Validate(ScanPlan plan)
        {
            if(plan.Relationships().Count==0)throw new ArgumentException("Select at least one model relationship to scan.");
            if(plan.Profile!=ScanProfile.Advanced&&plan.WholeModelScope)throw new ArgumentException("Choose a floor or scoped zones. Entire-model scope is available in Advanced.");
            if(plan.Profile==ScanProfile.QuickCoordination) {
                if(plan.LevelIds.Count>1)throw new ArgumentException("Quick coordination scans one floor or selected zones.");
                if(plan.Zones.Any(z=>string.IsNullOrEmpty(z.LevelName)&&string.IsNullOrEmpty(z.WorksetName)))throw new ArgumentException("Quick zones need a floor or workset boundary.");
                if(plan.Mode!=ScanMode.HardOnly||plan.InsideLinks||plan.LinkToLink||plan.CandidatePolicy.IncludeGenericModels||plan.CandidatePolicy.IncludeInsulation)
                    throw new ArgumentException("Quick coordination uses hard clashes, host relationships and standard exclusions.");
            }
            if(plan.Profile==ScanProfile.DetailedCoordination&&(plan.Mode!=ScanMode.HardAndClearance||plan.LinkToLink||plan.ClearanceRuleKeys.Count==0))
                throw new ArgumentException("Detailed coordination requires selected clearance rules and host/internal-link relationships.");
            if(plan.Stage==ScanStage.LinkToLink&&(!plan.LinkToLink||plan.LinkPairs.Count==0||plan.HostToHost||plan.HostToLinks||plan.InsideLinks))throw new ArgumentException("Link-to-link is a separate job containing explicit link pairs only.");
            if(plan.Stage==ScanStage.InsulationClearance&&(!plan.CandidatePolicy.IncludeInsulation||plan.Mode!=ScanMode.HardAndClearance))
                throw new ArgumentException("Insulation requires its dedicated clearance pass.");
            if(plan.Zones.Any(z=>z.GridRangeMin!=""||z.GridRangeMax!=""))throw new ArgumentException("Grid-range zones are not supported. Choose a floor/workset zone.");
        }
        public static string Fingerprint(ScanPlan plan)=>ProjectClassificationStore.Hash(Newtonsoft.Json.JsonConvert.SerializeObject(plan));
    }
}
