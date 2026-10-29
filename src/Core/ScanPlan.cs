using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Core
{
    public enum ScanProfile { QuickCoordination, DetailedCoordination, Advanced }
    public enum ScanStage { HardClash, Coordination, Clearance, LinkToLink, InsulationClearance }

    /// <summary>Empty endpoint means host; all other endpoints are link INSTANCE unique IDs.</summary>
    public sealed class ScanModelPair
    {
        public string Source { get; }
        public string Target { get; }
        public ScanModelPair(string source, string target)
        {
            source = source ?? ""; target = target ?? "";
            Source = string.CompareOrdinal(source, target) <= 0 ? source : target;
            Target = string.CompareOrdinal(source, target) <= 0 ? target : source;
        }
        public bool Matches(string a, string b) => (Source == a && Target == b) || (Source == b && Target == a);
    }

    public sealed class ScanDisciplinePair
    {
        public Discipline Source { get; }
        public Discipline Target { get; }
        public ScanDisciplinePair(Discipline source, Discipline target) { Source = source; Target = target; }
        public bool Matches(Discipline a, Discipline b) => (Source == a && Target == b) || (Source == b && Target == a);
    }

    public sealed class ScanZone
    {
        public string Name { get; }
        public string LevelName { get; }
        public string GridRangeMin { get; }
        public string GridRangeMax { get; }
        public string WorksetName { get; }
        public string LinkedModel { get; }
        public ScanZone(CoordinationZone zone)
        {
            Name=zone.Name; LevelName=zone.LevelName; GridRangeMin=zone.GridRangeMin;
            GridRangeMax=zone.GridRangeMax; WorksetName=zone.WorksetName; LinkedModel=zone.LinkedModel;
        }
        public CoordinationZone ToZone() => new CoordinationZone {Name=Name,LevelName=LevelName,
            GridRangeMin=GridRangeMin,GridRangeMax=GridRangeMax,WorksetName=WorksetName,LinkedModel=LinkedModel};
        public override string ToString()=>Name;
    }

    /// <summary>Detached, immutable input shared by future estimation and current execution.</summary>
    public sealed class ScanPlan
    {
        public int Version => 4;
        public IReadOnlyList<ScanCoordinationArea> CoordinationAreas { get; }
        public double CoordinationRadiusMM { get; }
        public string DocumentKey { get; }
        public ScanProfile Profile { get; }
        public ScanStage Stage { get; }
        public ScanMode Mode { get; }
        public bool HostToHost { get; }
        public bool HostToLinks { get; }
        public bool InsideLinks { get; }
        public bool LinkToLink { get; }
        public IReadOnlyList<string> SelectedLinkInstanceIds { get; }
        public IReadOnlyList<ScanModelPair> LinkPairs { get; }
        public IReadOnlyList<string> LevelIds { get; }
        public IReadOnlyList<ScanZone> Zones { get; }
        public IReadOnlyList<ScanDisciplinePair> DisciplinePairs { get; }
        public IReadOnlyList<Discipline> Disciplines { get; }
        public IReadOnlyList<ScanModelPair> ExcludedRelationships { get; }
        public IReadOnlyList<ScanDisciplineRule> DisciplineRules { get; }
        public string ProjectMappingsFingerprint { get; }
        public IReadOnlyList<long> CategoryIds { get; }
        public IReadOnlyList<string> ClearanceRuleKeys { get; }
        public string RuleSetName { get; }
        public string RuleSetContent { get; }
        public CoordinationCandidatePolicy CandidatePolicy { get; }
        public IReadOnlyList<ClassificationMapping> Mappings { get; }
        public IReadOnlyList<string> MedicalGasKeywords { get; }
        public IReadOnlyList<string> GravityDrainageKeywords { get; }
        public double MinimumOverlapMM3 { get; }
        public double InsulationMM { get; }
        public double MaintenanceMM { get; }
        public bool ExcludeConnectedJoints { get; }
        public bool WholeModelScope => LevelIds.Count==0 && Zones.Count==0 && CoordinationAreas.Count==0;

        public ScanPlan(string documentKey, ScanProfile profile, ScanStage stage, ScanMode mode,
            bool hostToHost, bool hostToLinks, bool insideLinks, bool linkToLink,
            IEnumerable<string> selectedLinkInstanceIds, IEnumerable<ScanModelPair>? linkPairs=null,
            IEnumerable<string>? levelIds=null, IEnumerable<ScanZone>? zones=null,
            IEnumerable<ScanDisciplinePair>? disciplinePairs=null, IEnumerable<long>? categoryIds=null,
            IEnumerable<string>? clearanceRuleKeys=null, string ruleSetName="DefaultRules", string ruleSetContent="",
            CoordinationCandidatePolicy? candidatePolicy=null, IEnumerable<ClassificationMapping>? mappings=null,
            IEnumerable<string>? medicalGasKeywords=null, IEnumerable<string>? gravityDrainageKeywords=null,
            double minimumOverlapMM3=1, double insulationMM=50, double maintenanceMM=100, bool excludeConnectedJoints=true,
            IEnumerable<ScanDisciplineRule>? disciplineRules=null,string projectMappingsFingerprint="",
            IEnumerable<Discipline>? disciplines=null,IEnumerable<ScanModelPair>? excludedRelationships=null,
            IEnumerable<ScanCoordinationArea>? coordinationAreas=null,double coordinationRadiusMM=2000)
        {
            DocumentKey=documentKey ?? throw new ArgumentNullException(nameof(documentKey));
            Profile=profile; Stage=stage; Mode=mode; HostToHost=hostToHost; HostToLinks=hostToLinks;
            InsideLinks=insideLinks; LinkToLink=linkToLink;
            SelectedLinkInstanceIds=Freeze(selectedLinkInstanceIds.Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s=>s,StringComparer.Ordinal));
            var pairs=(linkPairs ?? Enumerable.Empty<ScanModelPair>()).ToList();
            if(pairs.Any(p=>p.Source=="" || p.Source==p.Target || !SelectedLinkInstanceIds.Contains(p.Source) || !SelectedLinkInstanceIds.Contains(p.Target)))
                throw new ArgumentException("Link pairs require two different selected link instance IDs.",nameof(linkPairs));
            LinkPairs=Freeze(pairs.GroupBy(p=>Tuple.Create(p.Source,p.Target)).Select(g=>g.First()).OrderBy(p=>p.Source,StringComparer.Ordinal).ThenBy(p=>p.Target,StringComparer.Ordinal));
            LevelIds=Freeze((levelIds ?? Enumerable.Empty<string>()).Where(s=>s!="").Distinct().OrderBy(s=>s,StringComparer.Ordinal));
            Zones=Freeze(zones); DisciplinePairs=Freeze(disciplinePairs); CategoryIds=Freeze(categoryIds);
            DisciplineRules=Freeze(disciplineRules??CoordinationPairDefaults.Rules); ProjectMappingsFingerprint=projectMappingsFingerprint;
            Disciplines=Freeze(disciplines);ExcludedRelationships=Freeze(excludedRelationships);
            CoordinationAreas=Freeze(coordinationAreas);
            if(double.IsNaN(coordinationRadiusMM)||double.IsInfinity(coordinationRadiusMM)||coordinationRadiusMM<=0||coordinationRadiusMM>50000)throw new ArgumentOutOfRangeException(nameof(coordinationRadiusMM));
            CoordinationRadiusMM=coordinationRadiusMM;
            ClearanceRuleKeys=Freeze(clearanceRuleKeys); RuleSetName=ruleSetName; RuleSetContent=ruleSetContent;
            CandidatePolicy=candidatePolicy ?? new CoordinationCandidatePolicy(); Mappings=Freeze(mappings);
            MedicalGasKeywords=Freeze(medicalGasKeywords); GravityDrainageKeywords=Freeze(gravityDrainageKeywords);
            if(minimumOverlapMM3<0 || insulationMM<0 || maintenanceMM<0 ||
                double.IsNaN(minimumOverlapMM3+insulationMM+maintenanceMM) || double.IsInfinity(minimumOverlapMM3+insulationMM+maintenanceMM))
                throw new ArgumentOutOfRangeException(nameof(minimumOverlapMM3));
            MinimumOverlapMM3=minimumOverlapMM3; InsulationMM=insulationMM; MaintenanceMM=maintenanceMM;
            ExcludeConnectedJoints=excludeConnectedJoints;
        }
        private static IReadOnlyList<T> Freeze<T>(IEnumerable<T>? values) => Array.AsReadOnly((values ?? Enumerable.Empty<T>()).ToArray());
        public IReadOnlyList<ScanModelPair> Relationships()
        {
            var pairs=new List<ScanModelPair>();
            if(HostToHost)pairs.Add(new ScanModelPair("",""));
            if(HostToLinks)pairs.AddRange(SelectedLinkInstanceIds.Select(id=>new ScanModelPair("",id)));
            if(InsideLinks)pairs.AddRange(SelectedLinkInstanceIds.Select(id=>new ScanModelPair(id,id)));
            if(LinkToLink)pairs.AddRange(LinkPairs);
            return pairs.Where(p=>!ExcludedRelationships.Any(e=>e.Matches(p.Source,p.Target))).ToList().AsReadOnly();
        }
    }
}
