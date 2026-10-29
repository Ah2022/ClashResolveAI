using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClashResolveAI.Core
{
    public enum CoordinationElementKind { Unsupported, Mep, Pipe, Framing, Column, Wall, Floor, Ceiling, GenericModel, Insulation }
    public enum ClassificationEvidence { Unknown, RevitCategory, RevitSystemClassification, SystemType, FamilyTypeKeyword, ProjectMapping }

    public sealed class ElementClassification
    {
        public Discipline Discipline { get; }
        public long CategoryId { get; }
        public string Category { get; }
        public string SystemType { get; }
        public string SystemClassification { get; }
        public string FamilyType { get; }
        public bool IsCoordinationCandidate { get; }
        public string ExclusionReason { get; }
        public double Confidence { get; }
        public ClassificationEvidence Evidence { get; }
        public string Explanation { get; }
        public string MappingId { get; }
        public string CategoryAlias { get; }
        public CoordinationElementKind Kind { get; }
        public ElementClassification(Discipline discipline, long categoryId, string category, string systemType,
            string systemClassification, string familyType, bool isCoordinationCandidate, string exclusionReason,
            double confidence, ClassificationEvidence evidence, string explanation, string mappingId="",string categoryAlias="",CoordinationElementKind kind=CoordinationElementKind.Unsupported)
        {
            Discipline=discipline; CategoryId=categoryId; Category=category; SystemType=systemType;
            SystemClassification=systemClassification; FamilyType=familyType; IsCoordinationCandidate=isCoordinationCandidate;
            ExclusionReason=exclusionReason; Confidence=confidence; Evidence=evidence; Explanation=explanation; MappingId=mappingId;
            CategoryAlias=categoryAlias;Kind=kind;
        }
    }

    public enum FamilyTypeMatch { Contains, Exact }
    /// <summary>Immutable project override, including optional exact service context for confirmations.</summary>
    public sealed class ClassificationMapping
    {
        public string Id { get; }
        public long? CategoryId { get; }
        public string FamilyTypeContains { get; }
        public Discipline Discipline { get; }
        public bool Exclude { get; }
        public int Priority { get; }
        public FamilyTypeMatch MatchMode { get; }
        public string? SystemTypeEquals { get; }
        public string? SystemClassificationEquals { get; }
        public ClassificationMapping(string id, Discipline discipline, string familyTypeContains="", long? categoryId=null, bool exclude=false, int priority=0,
            FamilyTypeMatch matchMode=FamilyTypeMatch.Contains,string? systemTypeEquals=null,string? systemClassificationEquals=null)
        {
            if(string.IsNullOrWhiteSpace(id) || (!categoryId.HasValue && string.IsNullOrWhiteSpace(familyTypeContains)))
                throw new ArgumentException("Mappings require an ID and a category or family/type condition.");
            Id=id; Discipline=discipline; FamilyTypeContains=matchMode==FamilyTypeMatch.Exact?familyTypeContains:familyTypeContains.Trim(); CategoryId=categoryId; Exclude=exclude; Priority=priority;
            MatchMode=matchMode;SystemTypeEquals=systemTypeEquals;SystemClassificationEquals=systemClassificationEquals;
        }
        public bool Matches(ClassificationInput input) => (!CategoryId.HasValue || CategoryId==input.CategoryId) &&
            (FamilyTypeContains=="" || (MatchMode==FamilyTypeMatch.Exact?input.FamilyType.Equals(FamilyTypeContains,StringComparison.OrdinalIgnoreCase):input.FamilyType.IndexOf(FamilyTypeContains,StringComparison.OrdinalIgnoreCase)>=0)) &&
            (SystemTypeEquals==null||SystemTypeEquals.Equals(input.SystemType,StringComparison.OrdinalIgnoreCase)) &&
            (SystemClassificationEquals==null||SystemClassificationEquals.Equals(input.SystemClassification,StringComparison.OrdinalIgnoreCase));
        public int Specificity => FamilyTypeContains.Length*4+(MatchMode==FamilyTypeMatch.Exact?2:0)+(SystemTypeEquals!=null?1:0)+(SystemClassificationEquals!=null?1:0);
    }

    // Revit reads are kept in RevitElementClassifier; the decision policy is independently testable.
    public sealed class ClassificationInput
    {
        public long CategoryId { get; set; }
        public string Category { get; set; }="";
        public string CategoryAlias { get; set; }="";
        public string SystemType { get; set; }="";
        public string SystemClassification { get; set; }="";
        public string FamilyType { get; set; }="";
        public CoordinationElementKind Kind { get; set; }
        public Discipline CategoryDiscipline { get; set; }=Discipline.Unknown;
        public Discipline SystemDiscipline { get; set; }=Discipline.Unknown;
        public bool ConflictingSystems { get; set; }
    }

    public sealed class CoordinationCandidatePolicy
    {
        public bool IncludeStructural { get; }
        public bool IncludeGenericModels { get; }
        public bool IncludeInsulation { get; }
        public bool ExcludeNamedSupports { get; }
        public IReadOnlyList<long> RequiredEnvelopeCategoryIds { get; }
        public CoordinationCandidatePolicy(bool includeStructural=true, bool includeGenericModels=false,
            bool includeInsulation=false, bool excludeNamedSupports=true, IEnumerable<long>? requiredEnvelopeCategoryIds=null)
        {
            IncludeStructural=includeStructural; IncludeGenericModels=includeGenericModels;
            IncludeInsulation=includeInsulation; ExcludeNamedSupports=excludeNamedSupports;
            RequiredEnvelopeCategoryIds=Array.AsReadOnly((requiredEnvelopeCategoryIds ?? Enumerable.Empty<long>()).Distinct().ToArray());
        }
    }

    public static class CoordinationClassifier
    {
        private static readonly Regex Supports=new Regex(@"\b(HANGERS?|SUPPORTS?|SLEEVES?|PENETRATIONS?|ANCHORS?|BRACKETS?|RODS?|BLOCKOUTS?|CLAMPS?|TRAPEZES?)\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        public static ElementClassification Classify(ClassificationInput input, CoordinationCandidatePolicy policy,
            ScanStage stage, IEnumerable<ClassificationMapping>? mappings=null,
            IEnumerable<string>? medicalKeywords=null, IEnumerable<string>? drainageKeywords=null)
        {
            var matches=(mappings ?? Enumerable.Empty<ClassificationMapping>()).Where(m=>m.Matches(input))
                .OrderByDescending(m=>m.Priority).ThenByDescending(m=>m.Specificity).ThenBy(m=>m.Id,StringComparer.Ordinal).ToList();
            var mapping=matches.FirstOrDefault();
            Discipline discipline=input.Kind==CoordinationElementKind.GenericModel?Discipline.Unknown:input.CategoryDiscipline;
            var evidence=discipline==Discipline.Unknown?ClassificationEvidence.Unknown:ClassificationEvidence.RevitCategory;
            double confidence=discipline==Discipline.Unknown?0:0.95;
            string explanation=discipline==Discipline.Unknown?"No authoritative classification.":"Revit category.";
            string reason="";
            if(mapping!=null) {
                discipline=mapping.Discipline; evidence=ClassificationEvidence.ProjectMapping; confidence=1;
                explanation="Explicit project mapping: "+mapping.Id;
                if(matches.Any(m=>m.Priority==mapping.Priority && m.Specificity==mapping.Specificity &&
                    (m.Discipline!=mapping.Discipline || m.Exclude!=mapping.Exclude))) {
                    discipline=Discipline.Unknown; confidence=0; reason="Conflicting project mappings require review.";
                }
                else if(mapping.Exclude)reason="Excluded by project mapping: "+mapping.Id;
            }
            else if(input.ConflictingSystems) {
                discipline=Discipline.Unknown; confidence=0; evidence=ClassificationEvidence.Unknown;
                reason="Conflicting Revit systems require a project mapping."; explanation=reason;
            }
            else if(input.Kind!=CoordinationElementKind.GenericModel) {
                if(input.SystemDiscipline!=Discipline.Unknown && (input.Kind==CoordinationElementKind.Pipe || input.CategoryDiscipline==Discipline.Unknown)) {
                    discipline=input.SystemDiscipline; evidence=ClassificationEvidence.RevitSystemClassification; confidence=0.98;
                    explanation="Revit system classification.";
                } else if(input.Kind==CoordinationElementKind.Pipe || input.CategoryDiscipline==Discipline.Unknown) {
                    var typeGuess=Guess(input.SystemType,medicalKeywords,drainageKeywords);
                    var familyGuess=Guess(input.FamilyType,medicalKeywords,drainageKeywords);
                    var guess=typeGuess!=Discipline.Unknown?typeGuess:familyGuess;
                    if(guess!=Discipline.Unknown) {
                        discipline=guess; evidence=typeGuess!=Discipline.Unknown?ClassificationEvidence.SystemType:ClassificationEvidence.FamilyTypeKeyword;
                        confidence=typeGuess!=Discipline.Unknown?0.70:0.50; explanation=typeGuess!=Discipline.Unknown?"System type keyword fallback.":"Family/type keyword fallback; review recommended.";
                    } else if(input.Kind==CoordinationElementKind.Pipe) {
                        discipline=Discipline.Unknown; evidence=ClassificationEvidence.Unknown; confidence=0;
                        explanation="Pipe category does not establish the service; system classification or project mapping is required.";
                    }
                }
            }
            if(reason=="" && input.Kind==CoordinationElementKind.Unsupported)reason="Nonphysical or unsupported coordination category.";
            if(reason=="" && policy.ExcludeNamedSupports && Supports.IsMatch(input.FamilyType.Replace('_',' ').Replace('-',' ')))reason="Construction aid or support family.";
            if(reason=="" && input.Kind==CoordinationElementKind.GenericModel && (!policy.IncludeGenericModels || mapping==null))reason=mapping==null?"Generic Model requires an explicit project mapping.":"Generic models excluded by scan policy.";
            if(reason=="" && input.Kind==CoordinationElementKind.Insulation && (!policy.IncludeInsulation || stage!=ScanStage.InsulationClearance))reason="Insulation requires a dedicated insulation-clearance pass.";
            if(reason=="" && discipline==Discipline.Structural && !policy.IncludeStructural)reason="Structural elements excluded by scan policy.";
            if(reason=="" && (input.Kind==CoordinationElementKind.Wall || input.Kind==CoordinationElementKind.Floor || input.Kind==CoordinationElementKind.Ceiling) &&
                !policy.RequiredEnvelopeCategoryIds.Contains(input.CategoryId))reason="Category is not required by the selected rule set.";
            if(reason=="" && discipline==Discipline.Unknown)reason="Unknown classification requires coordinator review.";
            return new ElementClassification(discipline,input.CategoryId,input.Category,input.SystemType,input.SystemClassification,input.FamilyType,
                reason=="",reason,confidence,evidence,explanation,mapping?.Id ?? "",input.CategoryAlias,input.Kind);
        }
        private static Discipline Guess(string text,IEnumerable<string>? medical,IEnumerable<string>? drainage)
        {
            bool Match(IEnumerable<string>? terms) => (terms ?? Enumerable.Empty<string>()).Any(t=>!string.IsNullOrWhiteSpace(t) &&
                Regex.IsMatch(text,@"(?<![\p{L}\p{N}])"+Regex.Escape(t.Trim())+@"(?![\p{L}\p{N}])",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant));
            bool med=Match(medical),drain=Match(drainage);
            return med==drain?Discipline.Unknown:med?Discipline.MedicalGas:Discipline.GravityDrainage;
        }
    }
}
