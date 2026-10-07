using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Links;
using ClashResolveAI.Rules;
using Newtonsoft.Json;
using System;
using System.Linq;

namespace ClashResolveAI.Dashboard.Persistence
{
    internal static class RevitScanCapture
    {
        private static string ModelVersion(Document doc)
        {
            try {using var version=Document.GetDocumentVersion(doc);return version.VersionGUID+"|"+version.NumberOfSaves;}
            catch(Exception ex){Diagnostics.Log("Document version unavailable for scan metadata",ex);return "Unavailable";}
        }
        // Called only in Revit API context. Explicit allowlist prevents API keys/settings secrets entering history.
        public static ScanCapture Capture(Document doc,AppSettings settings,bool includeLinks,string level,CoordinationZone? zone,ScanMode mode,bool includeLinkToLink)
        {
            var configuration=new { settings.RuleSetName,Mode=mode.ToString(),includeLinks,includeLinkToLink,
                settings.ScanWithinLinks,settings.IncludeStructural,settings.IncludeGenericModels,settings.IncludeInsulation,
                settings.ExcludeConnectedJoints,settings.ExcludeNamedSupports,settings.MinimumOverlapMM3,settings.InsulationMM,settings.MaintenanceMM,
                RuleContentFingerprint=HistoryHash.Of(RulesEngine.CacheKey(settings.RuleSetName)) };
            var links=new LinkedModelManager(doc).GetAllLinks().OrderBy(l=>l.Instance.UniqueId,StringComparer.Ordinal).Select(l=> {
                var transform=l.Transform;
                string version="";
                if(l.IsLoaded)version=ModelVersion(l.LinkDoc)+"|"+ScanCoordinator.DocumentRevision(DocumentSession.Key(l.LinkDoc));
                return new { InstanceUniqueId=l.Instance.UniqueId,InstanceVersion=l.Instance.VersionGuid.ToString(),l.FileName,l.IsLoaded,
                    DocumentKey=l.IsLoaded?DocumentSession.Key(l.LinkDoc):"",Version=version,
                    Transform=new[]{transform.Origin,transform.BasisX,transform.BasisY,transform.BasisZ}.Select(p=>new[]{p.X,p.Y,p.Z}).ToArray() };
            }).ToList();
            string hostVersion=ModelVersion(doc);
            string centralPath="";
            if(doc.IsWorkshared){var path=doc.GetWorksharingCentralModelPath();if(path!=null)centralPath=ModelPathUtils.ConvertModelPathToUserVisiblePath(path);}
            string configJson=JsonConvert.SerializeObject(configuration),linksJson=JsonConvert.SerializeObject(links);
            return new ScanCapture {
                DocumentKey=DocumentSession.Key(doc),DisplayName=doc.Title,
                DocumentIdentityJson=JsonConvert.SerializeObject(new { ProjectUniqueId=doc.ProjectInformation.UniqueId,Path=doc.PathName,CentralPath=centralPath,
                    Policy="project-path-v1; Save As starts a separate lineage on reopen; no automatic aliasing" }),
                ModelFingerprint=HistoryHash.Of(hostVersion+"|"+ScanCoordinator.DocumentRevision(DocumentSession.Key(doc))+"|"+linksJson),
                ModelFingerprintKind=hostVersion=="Unavailable"?"SessionRevisionOnly":"VersionAndRevision",
                RuleSetName=settings.RuleSetName,ConfigurationJson=configJson,ConfigurationFingerprint=HistoryHash.Of(configJson),
                LinkedModelsJson=linksJson,ScanMode=mode.ToString(),EngineVersion=typeof(RevitScanCapture).Assembly.GetName().Version?.ToString()??"",
                ScopeJson=JsonConvert.SerializeObject(new {LevelId=level,Zone=zone}),FullModelScope=string.IsNullOrEmpty(level)&&zone==null
            };
        }
    }
}
