using ClashResolveAI.Rules;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Core
{
    public sealed class ScanDisciplineRule
    {
        public Discipline Source { get; }
        public Discipline Target { get; }
        public bool Check { get; }
        public string RuleKey { get; }
        public ScanDisciplineRule(Discipline source,Discipline target,bool check=true,string ruleKey="Generic")
        {Source=source;Target=target;Check=check;RuleKey=string.IsNullOrEmpty(ruleKey)?"Generic":ruleKey;}
        public bool Matches(Discipline a,Discipline b)=>(Source==a&&Target==b)||(Source==b&&Target==a);
    }
    public static class CoordinationPairDefaults
    {
        public static IReadOnlyList<ScanDisciplineRule> Rules { get; }=Array.AsReadOnly(new[]{
            new ScanDisciplineRule(Discipline.Plumbing,Discipline.Plumbing),new ScanDisciplineRule(Discipline.HVAC,Discipline.HVAC),
            new ScanDisciplineRule(Discipline.Electrical,Discipline.Electrical),new ScanDisciplineRule(Discipline.GravityDrainage,Discipline.GravityDrainage),
            new ScanDisciplineRule(Discipline.FireProtection,Discipline.FireProtection),new ScanDisciplineRule(Discipline.MedicalGas,Discipline.MedicalGas),
            new ScanDisciplineRule(Discipline.CableTray,Discipline.CableTray),new ScanDisciplineRule(Discipline.Conduit,Discipline.Conduit),
            new ScanDisciplineRule(Discipline.Plumbing,Discipline.Structural,ruleKey:"Pipe_vs_Beam"),
            new ScanDisciplineRule(Discipline.HVAC,Discipline.Structural,ruleKey:"Duct_vs_Beam"),
            new ScanDisciplineRule(Discipline.Electrical,Discipline.Structural),
            new ScanDisciplineRule(Discipline.FireProtection,Discipline.Structural,ruleKey:"FireMain_vs_Structure"),
            new ScanDisciplineRule(Discipline.Electrical,Discipline.Plumbing,ruleKey:"CableTray_vs_Pipe"),
            new ScanDisciplineRule(Discipline.Plumbing,Discipline.HVAC),new ScanDisciplineRule(Discipline.Electrical,Discipline.HVAC),
            new ScanDisciplineRule(Discipline.FireProtection,Discipline.Electrical,ruleKey:"Sprinkler_vs_Light"),
            new ScanDisciplineRule(Discipline.FireProtection,Discipline.HVAC),
            new ScanDisciplineRule(Discipline.GravityDrainage,Discipline.Structural,ruleKey:"Pipe_vs_Beam"),
            new ScanDisciplineRule(Discipline.GravityDrainage,Discipline.HVAC),new ScanDisciplineRule(Discipline.GravityDrainage,Discipline.Electrical),
            new ScanDisciplineRule(Discipline.GravityDrainage,Discipline.Plumbing),
            new ScanDisciplineRule(Discipline.MedicalGas,Discipline.HVAC,ruleKey:"MedGas_vs_HVAC"),
            new ScanDisciplineRule(Discipline.MedicalGas,Discipline.Structural,ruleKey:"Pipe_vs_Beam"),
            new ScanDisciplineRule(Discipline.CableTray,Discipline.HVAC,ruleKey:"CableTray_vs_Pipe"),
            new ScanDisciplineRule(Discipline.CableTray,Discipline.Plumbing,ruleKey:"CableTray_vs_Pipe"),
            new ScanDisciplineRule(Discipline.CableTray,Discipline.Structural),new ScanDisciplineRule(Discipline.Conduit,Discipline.HVAC),
            new ScanDisciplineRule(Discipline.Conduit,Discipline.Structural),new ScanDisciplineRule(Discipline.Conduit,Discipline.Plumbing)
        });
    }
    /// <summary>All elements in a partition share the metadata on which rules may depend.</summary>
    public sealed class ClassifiedPartition
    {
        public Discipline Discipline { get; }
        public long CategoryId { get; }
        public string Category { get; }
        public string CategoryAlias { get; }
        public CoordinationElementKind Kind { get; }
        public string Key { get; }
        public ClassifiedPartition(ElementClassification value)
        {
            Discipline=value.Discipline;CategoryId=value.CategoryId;Category=value.Category;CategoryAlias=value.CategoryAlias;Kind=value.Kind;
            Key=((int)Discipline)+":"+CategoryId+":"+(int)Kind+":"+Uri.EscapeDataString(Category)+":"+Uri.EscapeDataString(CategoryAlias);
        }
        public IEnumerable<string> CategoryKeys() => new[]{CategoryId.ToString(System.Globalization.CultureInfo.InvariantCulture),Category,CategoryAlias}.Where(s=>s!="").Distinct();
    }
    public sealed class PairRuleDecision
    {
        public bool Allowed { get; }
        public string RuleKey { get; }
        public string RuleId { get; }
        public string Reason { get; }
        public double ClearanceMM { get; }
        public string Severity { get; }
        public bool ClearanceEnabled { get; }
        private readonly ClashRule _rule;
        internal PairRuleDecision(bool allowed,string key,string id,string reason,ClashRule? rule=null,bool clearanceEnabled=false)
        {
            Allowed=allowed;RuleKey=key;RuleId=id;Reason=reason;ClearanceMM=rule?.ClearanceMM??0;Severity=rule?.Severity??"";
            ClearanceEnabled=clearanceEnabled;
            _rule=JsonConvert.DeserializeObject<ClashRule>(JsonConvert.SerializeObject(rule??new ClashRule {Ignore=true}))!;
        }
        public ClashRule CopyRule() => new ClashRule {ClearanceMM=_rule.ClearanceMM,Severity=_rule.Severity,Priority=_rule.Priority,
            Moving=_rule.Moving,Note=_rule.Note,CodeRef=_rule.CodeRef,Ignore=_rule.Ignore};
    }
    /// <summary>Compiles rules once per metadata partition pair, before any spatial query.</summary>
    public sealed class CompiledPairPolicy
    {
        private readonly ScanPlan _plan;
        private readonly RuleSet _rules;
        private readonly Dictionary<string,PairRuleDecision> _decisions=new Dictionary<string,PairRuleDecision>(StringComparer.Ordinal);
        public int CompiledPairCount=>_decisions.Count;
        public CompiledPairPolicy(ScanPlan plan)
        {
            _plan=plan;_rules=JsonConvert.DeserializeObject<RuleSet>(plan.RuleSetContent)??throw new ArgumentException("Invalid rule set.");
            if(_rules.Rules==null||_rules.DisciplinePairs==null||_rules.CategoryPairs==null)throw new ArgumentException("Rule dictionaries cannot be null.");
            foreach(var entry in _rules.Rules.Concat(_rules.DisciplinePairs).Concat(_rules.CategoryPairs))
                if(entry.Value==null||entry.Value.Severity==null||double.IsNaN(entry.Value.ClearanceMM)||double.IsInfinity(entry.Value.ClearanceMM)||entry.Value.ClearanceMM<0)
                    throw new ArgumentException("Invalid clearance or severity in rule: "+entry.Key);
            foreach(string selected in plan.ClearanceRuleKeys)
                if(!RuleChoices().Contains(selected))throw new ArgumentException("Unknown selected clearance rule: "+selected);
        }
        public IReadOnlyList<string> RuleChoices() => _rules.Rules.Keys.Concat(_rules.DisciplinePairs.Keys.Select(k=>"DiscPair:"+k))
            .Concat(_rules.CategoryPairs.Keys.Select(k=>"CatPair:"+k)).OrderBy(k=>k,StringComparer.Ordinal).ToArray();
        public PairRuleDecision Resolve(ClassifiedPartition a,ClassifiedPartition b)
        {
            // Preserve source ordering for the legacy alias/AB-before-BA precedence.
            string cacheKey=a.Key+"|"+b.Key;
            if(_decisions.TryGetValue(cacheKey,out var result))return result;
            return _decisions[cacheKey]=Compile(a,b);
        }
        private PairRuleDecision Compile(ClassifiedPartition a,ClassifiedPartition b)
        {
            PairRuleDecision Deny(string reason)=>new PairRuleDecision(false,"Generic","",reason);
            if(a.Discipline==Discipline.Unknown||b.Discipline==Discipline.Unknown)return Deny("Unknown discipline.");
            if(_plan.Disciplines.Count>0&&(!_plan.Disciplines.Contains(a.Discipline)||!_plan.Disciplines.Contains(b.Discipline)))return Deny("Outside selected disciplines.");
            if(_plan.CategoryIds.Count>0&&(!_plan.CategoryIds.Contains(a.CategoryId)||!_plan.CategoryIds.Contains(b.CategoryId)))return Deny("Outside selected categories.");
            bool requested=_plan.DisciplinePairs.Any(p=>p.Matches(a.Discipline,b.Discipline));
            if(_plan.DisciplinePairs.Count>0&&!requested)return Deny("Outside selected discipline pairs.");
            if(_plan.Stage==ScanStage.InsulationClearance&&a.Kind!=CoordinationElementKind.Insulation&&b.Kind!=CoordinationElementKind.Insulation)
                return Deny("Dedicated insulation pass requires an insulation endpoint.");
            var matrix=_plan.DisciplineRules.FirstOrDefault(p=>p.Matches(a.Discipline,b.Discipline));
            string ruleKey=matrix?.RuleKey??"Generic";
            ClashRule? resolved=null;string id="";
            foreach(string x in a.CategoryKeys()) {
                foreach(string y in b.CategoryKeys()) {
                    if(_rules.CategoryPairs.TryGetValue(x+"|"+y,out resolved)){id="CatPair:"+x+"|"+y;break;}
                    if(_rules.CategoryPairs.TryGetValue(y+"|"+x,out resolved)){id="CatPair:"+y+"|"+x;break;}
                }
                if(resolved!=null)break;
            }
            if(resolved==null&&matrix?.Check==true&&ruleKey!="Generic"&&_rules.Rules.TryGetValue(ruleKey,out resolved))id=ruleKey;
            if(resolved==null) {
                if(_rules.DisciplinePairs.TryGetValue(a.Discipline+"|"+b.Discipline,out resolved))id="DiscPair:"+a.Discipline+"|"+b.Discipline;
                else if(_rules.DisciplinePairs.TryGetValue(b.Discipline+"|"+a.Discipline,out resolved))id="DiscPair:"+b.Discipline+"|"+a.Discipline;
            }
            if(resolved==null) {
                if(matrix?.Check!=true&&!requested)return Deny("Unsupported discipline pair.");
                if(!_rules.Rules.TryGetValue("Generic",out resolved))resolved=new ClashRule {ClearanceMM=DefaultClearance(a.Discipline,b.Discipline)};
                id="Generic";
            }
            if(resolved.Ignore || string.Equals(resolved.Severity,"Ignore",StringComparison.OrdinalIgnoreCase))return new PairRuleDecision(false,ruleKey,id,"Ignored by "+id,resolved);
            bool clearance=_plan.Mode==ScanMode.HardAndClearance&&(_plan.ClearanceRuleKeys.Count==0||SelectedRule(id));
            if((_plan.Stage==ScanStage.Clearance||_plan.Stage==ScanStage.InsulationClearance)&&!clearance)return new PairRuleDecision(false,ruleKey,id,"Clearance rule not selected.",resolved);
            if(double.IsNaN(resolved.ClearanceMM)||double.IsInfinity(resolved.ClearanceMM)||resolved.ClearanceMM<0)throw new ArgumentException("Invalid clearance in "+id);
            return new PairRuleDecision(true,ruleKey,id,"Allowed by "+id,resolved,clearance);
        }
        private bool SelectedRule(string id)
        {
            if(_plan.ClearanceRuleKeys.Contains(id))return true;
            int colon=id.IndexOf(':');
            if(colon<0||(!id.StartsWith("CatPair:",StringComparison.Ordinal)&&!id.StartsWith("DiscPair:",StringComparison.Ordinal)))return false;
            var endpoints=id.Substring(colon+1).Split('|');
            return endpoints.Length==2&&_plan.ClearanceRuleKeys.Contains(id.Substring(0,colon+1)+endpoints[1]+"|"+endpoints[0]);
        }
        private static double DefaultClearance(Discipline a,Discipline b)
        {
            switch(PriorityMatrix.Get(a)<=PriorityMatrix.Get(b)?a:b) {
                case Discipline.HVAC:return .164*304.8;case Discipline.GravityDrainage:return .246*304.8;
                case Discipline.FireProtection:return .328*304.8;case Discipline.MedicalGas:return .492*304.8;
                case Discipline.Plumbing:return .098*304.8;case Discipline.Electrical:return .197*304.8;default:return .164*304.8;
            }
        }
    }
}
