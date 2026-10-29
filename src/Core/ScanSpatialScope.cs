using BIMEngine.Core.Spatial;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Core
{
    public sealed class ScanFloor
    {
        public string Id { get; }
        public string Name { get; }
        public double Elevation { get; }
        public ScanFloor(string id,string name,double elevation){Id=id;Name=name;Elevation=elevation;}
        public override string ToString()=>Name;
    }
    public sealed class ScanSpatialScope
    {
        private readonly ScanPlan _plan;
        private readonly Dictionary<string,Tuple<double,double>> _ids=new Dictionary<string,Tuple<double,double>>();
        private readonly Dictionary<string,Tuple<double,double>> _names=new Dictionary<string,Tuple<double,double>>();
        public ScanSpatialScope(ScanPlan plan,IEnumerable<ScanFloor> floors)
        {
            _plan=plan;var ordered=floors.OrderBy(f=>f.Elevation).ToArray();
            foreach(var floor in ordered){var band=Tuple.Create(floor.Elevation,ordered.FirstOrDefault(f=>f.Elevation>floor.Elevation+.1)?.Elevation??floor.Elevation+16.4);_ids[floor.Id]=band;_names[floor.Name]=band;}
            if(plan.LevelIds.Any(id=>!_ids.ContainsKey(id))||plan.Zones.Any(z=>z.LevelName!=""&&!_names.ContainsKey(z.LevelName)))throw new ArgumentException("A selected floor no longer exists.");
        }
        public bool Includes(AABB bounds,string sourceModelName,string targetModelName,string workset)
        {
            bool At(Tuple<double,double> band)=>bounds.MaxZ>=band.Item1&&bounds.MinZ<band.Item2;
            bool Zone(ScanZone z)=>(z.LevelName==""||At(_names[z.LevelName]))&&(z.WorksetName==""||z.WorksetName==workset)&&
                (z.LinkedModel==""||string.Equals(z.LinkedModel,sourceModelName!=""?sourceModelName:targetModelName,StringComparison.OrdinalIgnoreCase)||
                 string.Equals(z.LinkedModel,System.IO.Path.GetFileNameWithoutExtension(sourceModelName!=""?sourceModelName:targetModelName),StringComparison.OrdinalIgnoreCase));
            return (_plan.LevelIds.Count==0||_plan.LevelIds.Any(id=>At(_ids[id])))&&(_plan.Zones.Count==0||_plan.Zones.Any(Zone))&&
                (_plan.CoordinationAreas.Count==0||_plan.CoordinationAreas.Any(a=>a.Bounds().Intersects(bounds)));
        }
    }
    public static class ScanPlanEligibility
    {
        public static bool Includes(ScanPlan plan,ElementClassification classification)=>classification.IsCoordinationCandidate&&
            (plan.CategoryIds.Count==0||plan.CategoryIds.Contains(classification.CategoryId))&&
            (plan.Disciplines.Count==0||plan.Disciplines.Contains(classification.Discipline))&&
            (plan.DisciplinePairs.Count==0||plan.DisciplinePairs.Any(p=>p.Source==classification.Discipline||p.Target==classification.Discipline));
    }
}
