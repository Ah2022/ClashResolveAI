using Autodesk.Revit.DB;
using ClashResolveAI.Links;
using ClashResolveAI.Rules;
using System.Collections.Generic;
using System.Linq;
using System;
using System.IO;

namespace ClashResolveAI.Core
{
    internal static class RevitScanPlanFactory
    {
        // Full Scan owns detached display records; never mutate the shared Live link cache.
        internal static List<LinkedModel> Links(Document doc)=>new LinkedModelManager(doc).GetAllLinks().Select(link=> {
            string name=link.FileName;
            try {
                var type=(RevitLinkType)doc.GetElement(link.Instance.GetTypeId());
                name=Path.GetFileName(ModelPathUtils.ConvertModelPathToUserVisiblePath(type.GetExternalFileReference().GetAbsolutePath()));
            }catch(Exception){if(link.IsLoaded)name=Path.GetFileName(link.LinkDoc.PathName);}
            if(string.IsNullOrEmpty(name))name=link.IsLoaded?link.LinkDoc.Title:link.Instance.Name;
            return new LinkedModel {Instance=link.Instance,LinkDoc=link.LinkDoc,Transform=link.Transform,FileName=name,Discipline=link.Discipline,IsLoaded=link.IsLoaded};
        }).ToList();
        public static ScanPlan Create(Document doc, AppSettings settings, IEnumerable<LinkedModel> loadedLinks,
            bool includeLinks, string levelId="", CoordinationZone? zone=null, ScanMode? mode=null,
            bool? linkToLink=null, IEnumerable<string>? selectedLinks=null, IEnumerable<ScanModelPair>? pairs=null,
            ScanStage? stage=null, IEnumerable<ClassificationMapping>? mappings=null)
        {
            settings=settings.FullScanSnapshot();
            var rules=ScanSessionCache.Rules(doc,settings.RuleSetName);
            var keywords=SystemKeywordConfig.Load();
            var projectMappings=mappings==null?new ProjectClassificationStore().Load(DocumentSession.Key(doc)):null;
            var selected=(selectedLinks ?? loadedLinks.Select(l=>l.Instance.UniqueId)).ToArray();
            var effectiveMode=mode ?? settings.FullScanMode;
            var effectiveStage=stage ?? (effectiveMode==ScanMode.HardOnly?ScanStage.HardClash:ScanStage.Coordination);
            return new ScanPlan(DocumentSession.Key(doc),ScanProfile.Advanced,effectiveStage,effectiveMode,
                settings.ScanHostToHost,includeLinks && settings.ScanHostToLinks,includeLinks && settings.FullScanWithinLinks,
                includeLinks && (linkToLink ?? settings.IncludeLinkToLink),includeLinks?selected:new string[0],
                includeLinks?pairs:null,levelId==""?null:new[]{levelId},zone==null?null:new[]{new ScanZone(zone)},
                ruleSetName:settings.RuleSetName,ruleSetContent:rules.SnapshotContent,
                candidatePolicy:new CoordinationCandidatePolicy(settings.IncludeStructural,settings.FullScanIncludeGenericModels,
                    settings.FullScanIncludeInsulation,settings.FullScanExcludeNamedSupports,rules.RequiredEnvelopeCategories()),mappings:mappings??projectMappings!.Mappings,
                medicalGasKeywords:keywords.MedicalGasKeywords,gravityDrainageKeywords:keywords.GravityDrainageKeywords,
                minimumOverlapMM3:settings.MinimumOverlapMM3,insulationMM:settings.InsulationMM,maintenanceMM:settings.MaintenanceMM,
                excludeConnectedJoints:settings.ExcludeConnectedJoints,
                disciplineRules:ElementCollector.ClashMatrix.Select(m=>new ScanDisciplineRule(m.Source,m.Target,m.Check,m.RuleKey)),
                projectMappingsFingerprint:projectMappings?.Fingerprint??"");
        }
    }
}
