using BIMEngine.Core.Spatial;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Core
{
    public sealed class ScanCoordinationArea
    {
        public string EvidenceId { get; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public double RadiusFeet { get; }
        public ScanCoordinationArea(string evidenceId,double x,double y,double z,double radiusFeet)
        {
            if(new[]{x,y,z,radiusFeet}.Any(v=>double.IsNaN(v)||double.IsInfinity(v))||radiusFeet<=0)throw new ArgumentException("Invalid confirmed coordination area.");
            EvidenceId=evidenceId;X=x;Y=y;Z=z;RadiusFeet=radiusFeet;
        }
        public AABB Bounds()=>new AABB(X-RadiusFeet,Y-RadiusFeet,Z-RadiusFeet,X+RadiusFeet,Y+RadiusFeet,Z+RadiusFeet);
    }
    public static class ScanStaging
    {
        public static bool IsStaged(ScanPlan plan)=>plan.Profile==ScanProfile.DetailedCoordination&&plan.Stage==ScanStage.Coordination;
        public static bool AllowsPair(ScanPlan plan,AABB a,AABB b)=>plan.CoordinationAreas.Count==0||plan.CoordinationAreas.Any(area=>area.Bounds().Intersects(a)&&area.Bounds().Intersects(b));
        public static ScanPlan HardPass(ScanPlan plan)=>Copy(plan,ScanStage.HardClash,ScanMode.HardOnly,null);
        public static ScanPlan? ClearancePass(ScanPlan plan,IEnumerable<ClashResult> hardResults)
        {
            var areas=hardResults.Where(c=>c.TestType==ClashTestType.HardClash&&c.OverlapVolumeMM3>=Math.Max(.001,plan.MinimumOverlapMM3))
                .Select(c=>new ScanCoordinationArea(c.ClashId,c.ClashPoint.X,c.ClashPoint.Y,c.ClashPoint.Z,plan.CoordinationRadiusMM/304.8)).ToArray();
            return areas.Length==0?null:Copy(plan,ScanStage.Clearance,ScanMode.HardAndClearance,areas);
        }
        private static ScanPlan Copy(ScanPlan p,ScanStage stage,ScanMode mode,IEnumerable<ScanCoordinationArea>? areas)=>
            new ScanPlan(p.DocumentKey,ScanProfile.Advanced,stage,mode,p.HostToHost,p.HostToLinks,p.InsideLinks,false,p.SelectedLinkInstanceIds,
                levelIds:p.LevelIds,zones:p.Zones,disciplinePairs:p.DisciplinePairs,categoryIds:p.CategoryIds,
                clearanceRuleKeys:mode==ScanMode.HardOnly?null:p.ClearanceRuleKeys,ruleSetName:p.RuleSetName,ruleSetContent:p.RuleSetContent,candidatePolicy:p.CandidatePolicy,
                mappings:p.Mappings,medicalGasKeywords:p.MedicalGasKeywords,gravityDrainageKeywords:p.GravityDrainageKeywords,minimumOverlapMM3:p.MinimumOverlapMM3,
                insulationMM:p.InsulationMM,maintenanceMM:p.MaintenanceMM,excludeConnectedJoints:p.ExcludeConnectedJoints,disciplineRules:p.DisciplineRules,
                projectMappingsFingerprint:p.ProjectMappingsFingerprint,disciplines:p.Disciplines,excludedRelationships:p.ExcludedRelationships,
                coordinationAreas:areas,coordinationRadiusMM:p.CoordinationRadiusMM);
    }
}
