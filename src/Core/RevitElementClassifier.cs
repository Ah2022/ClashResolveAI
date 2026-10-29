using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;

namespace ClashResolveAI.Core
{
    /// <summary>Revit API context only; never requests geometry or bounding boxes.</summary>
    internal sealed class RevitElementClassifier
    {
        private readonly ScanPlan _plan;
        // Stable document identity also handles different managed wrappers of the same native model.
        private readonly Dictionary<string,Dictionary<long,ElementClassification>> _cache=new Dictionary<string,Dictionary<long,ElementClassification>>();
        public int ClassificationCount { get; private set; }
        public RevitElementClassifier(ScanPlan plan) { _plan=plan; }
        public ElementClassification Get(Element element)
        {
            string documentKey=DocumentSession.Key(element.Document);
            if(!_cache.TryGetValue(documentKey,out var document))_cache[documentKey]=document=new Dictionary<long,ElementClassification>();
            if(document.TryGetValue(element.Id.Value,out var cached))return cached;
            ClassificationInput input;
            try {input=ReadInput(element);}
            catch(Exception ex) {
                Diagnostics.Log("Classification unavailable for element "+element.Id.Value,ex);
                var failed=new ElementClassification(Discipline.Unknown,element.Category?.Id.Value ?? 0,element.Category?.Name ?? "",
                    "","",ElementCollector.GetFamilyTypeName(element),false,"Classification data unavailable; review required.",0,
                    ClassificationEvidence.Unknown,ex.Message);
                document[element.Id.Value]=failed; ClassificationCount++; return failed;
            }
            var classification=CoordinationClassifier.Classify(input,_plan.CandidatePolicy,_plan.Stage,_plan.Mappings,
                _plan.MedicalGasKeywords,_plan.GravityDrainageKeywords);
            document[element.Id.Value]=classification; ClassificationCount++; return classification;
        }
        internal static ClassificationInput ReadInput(Element element)
        {
            var input=new ClassificationInput {CategoryId=element.Category?.Id.Value ?? 0,
                Category=element.Category?.Name ?? "",CategoryAlias=ElementCollector.GetCategoryName(element),FamilyType=ElementCollector.GetFamilyTypeName(element)};
            var cat=(BuiltInCategory)input.CategoryId;
            switch(cat) {
                case BuiltInCategory.OST_GenericModel: case BuiltInCategory.OST_SpecialityEquipment:
                    input.Kind=CoordinationElementKind.GenericModel; break;
                case BuiltInCategory.OST_StructuralFraming: input.Kind=CoordinationElementKind.Framing; input.CategoryDiscipline=Discipline.Structural; break;
                case BuiltInCategory.OST_StructuralColumns: input.Kind=CoordinationElementKind.Column; input.CategoryDiscipline=Discipline.Structural; break;
                case BuiltInCategory.OST_Walls: input.Kind=CoordinationElementKind.Wall; input.CategoryDiscipline=Discipline.Structural; break;
                case BuiltInCategory.OST_Floors: input.Kind=CoordinationElementKind.Floor; input.CategoryDiscipline=Discipline.Structural; break;
                case BuiltInCategory.OST_Ceilings: input.Kind=CoordinationElementKind.Ceiling; input.CategoryDiscipline=Discipline.Structural; break;
                case BuiltInCategory.OST_PipeCurves: case BuiltInCategory.OST_FlexPipeCurves:
                case BuiltInCategory.OST_PipeFitting: case BuiltInCategory.OST_PipeAccessory: case BuiltInCategory.OST_PlumbingFixtures:
                    input.Kind=CoordinationElementKind.Pipe; break;
                case BuiltInCategory.OST_PipeInsulations: input.Kind=CoordinationElementKind.Insulation; input.CategoryDiscipline=Discipline.Plumbing; break;
                case BuiltInCategory.OST_DuctInsulations: case BuiltInCategory.OST_DuctLinings:
                    input.Kind=CoordinationElementKind.Insulation; input.CategoryDiscipline=Discipline.HVAC; break;
                default:
                    // Only unique-category disciplines are read here; generic/pipe heuristics are never invoked.
                    input.CategoryDiscipline=ElementCollector.InferDisciplineFromCategory(input.CategoryId);
                    input.Kind=input.CategoryDiscipline==Discipline.Unknown?CoordinationElementKind.Unsupported:CoordinationElementKind.Mep;
                    break;
            }
            // Prefer locale-independent MEPSystemType enum data over display text.
            foreach(var parameter in new[]{BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM,BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM}) {
                var p=element.get_Parameter(parameter);
                if(p==null || p.StorageType!=StorageType.ElementId)continue;
                if(!(element.Document.GetElement(p.AsElementId()) is MEPSystemType type))continue;
                input.SystemType=type.Name; input.SystemClassification=type.SystemClassification.ToString();
                input.SystemDiscipline=SystemDiscipline(type.SystemClassification); break;
            }
            if(input.SystemType=="") {
                var p=element.get_Parameter(BuiltInParameter.RBS_CTC_SERVICE_TYPE);
                input.SystemType=p?.AsString() ?? "";
            }
            // A fixture/equipment family can lack a type parameter even when its connectors have systems.
            if(input.SystemDiscipline==Discipline.Unknown && element is FamilyInstance instance && instance.MEPModel?.ConnectorManager!=null) {
                var disciplines=new HashSet<Discipline>();
                var classifications=new HashSet<string>();var systemTypes=new HashSet<string>();
                foreach(Connector connector in instance.MEPModel.ConnectorManager.Connectors) {
                    // Logical/non-MEP connectors do not expose a usable MEPSystem.
                    try {
                        var system=connector.MEPSystem;
                        if(system==null || !(element.Document.GetElement(system.GetTypeId()) is MEPSystemType type))continue;
                        var disc=SystemDiscipline(type.SystemClassification);
                        if(disc!=Discipline.Unknown)disciplines.Add(disc);
                        systemTypes.Add(type.Name);classifications.Add(type.SystemClassification.ToString());
                    } catch(Autodesk.Revit.Exceptions.InvalidOperationException) { }
                }
                if(disciplines.Count==1)foreach(var disc in disciplines)input.SystemDiscipline=disc;
                input.ConflictingSystems=disciplines.Count>1;
                if(classifications.Count>0) {
                    input.SystemClassification=string.Join("; ",System.Linq.Enumerable.OrderBy(classifications,c=>c,StringComparer.Ordinal));
                    input.SystemType=string.Join("; ",System.Linq.Enumerable.OrderBy(systemTypes,c=>c,StringComparer.Ordinal));
                }
            }
            return input;
        }
        internal static Discipline SystemDiscipline(MEPSystemClassification classification)
        {
            switch(classification) {
                case MEPSystemClassification.Sanitary: case MEPSystemClassification.Vent:
                case MEPSystemClassification.Storm: case MEPSystemClassification.CondensateDrain: return Discipline.GravityDrainage;
                case MEPSystemClassification.DomesticColdWater: case MEPSystemClassification.DomesticHotWater:
                case MEPSystemClassification.SupplyHydronic: case MEPSystemClassification.ReturnHydronic:
                case MEPSystemClassification.Recirculation: return Discipline.Plumbing;
                case MEPSystemClassification.FireProtectWet: case MEPSystemClassification.FireProtectDry:
                case MEPSystemClassification.FireProtectPreaction: case MEPSystemClassification.FireProtectOther:
                case MEPSystemClassification.FireAlarm: return Discipline.FireProtection;
                case MEPSystemClassification.SupplyAir: case MEPSystemClassification.ReturnAir:
                case MEPSystemClassification.ExhaustAir: case MEPSystemClassification.OtherAir: return Discipline.HVAC;
                case MEPSystemClassification.PowerCircuit: case MEPSystemClassification.PowerBalanced:
                case MEPSystemClassification.PowerUnBalanced: case MEPSystemClassification.DataCircuit:
                case MEPSystemClassification.Communication: case MEPSystemClassification.Controls:
                case MEPSystemClassification.NurseCall: case MEPSystemClassification.Security:
                case MEPSystemClassification.Telephone: return Discipline.Electrical;
                default:return Discipline.Unknown;
            }
        }
    }
}
