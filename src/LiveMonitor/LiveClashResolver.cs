using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Security.Cryptography;

namespace ClashResolveAI.LiveMonitor
{
    // API thread only. The document version is a supplementary signal; element
    // version GUIDs and DocumentChanged revisions also protect unsaved edits.
    internal static class LiveEnvironment
    {
        public static string Capture(Document doc)
            =>Capture(doc,false);
        internal static string CaptureForFullScan(Document doc)
            =>Capture(doc,true);
        private static string Capture(Document doc,bool fullScan)
        {
            var s=AppSettings.Load();
            var text=new StringBuilder(string.Join("|",s.RuleSetName,fullScan?s.FullScanMode:s.LiveMode,fullScan&&s.IncludeLinkToLink,s.ScanWithinLinks,s.IncludeStructural,s.ScanLinkedModels,
                s.IncludeGenericModels,s.IncludeInsulation,s.ExcludeConnectedJoints,s.ExcludeNamedSupports,
                s.MinimumOverlapMM3.ToString("R",CultureInfo.InvariantCulture),s.InsulationMM.ToString("R",CultureInfo.InvariantCulture),
                s.MaintenanceMM.ToString("R",CultureInfo.InvariantCulture),Rules.RulesEngine.CacheKey(s.RuleSetName)));
            text.Append("|hostInputs:").Append(ScanCoordinator.InputRevision(DocumentSession.Key(doc)));
            foreach(var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().OrderBy(l=>l.UniqueId,StringComparer.Ordinal)) {
                text.Append("|link:").Append(link.UniqueId).Append(':').Append(link.VersionGuid);
                var transform=link.GetTotalTransform();
                foreach(var p in new[]{transform.Origin,transform.BasisX,transform.BasisY,transform.BasisZ})
                    text.Append('|').Append(p.X.ToString("R",CultureInfo.InvariantCulture)).Append(',').Append(p.Y.ToString("R",CultureInfo.InvariantCulture)).Append(',').Append(p.Z.ToString("R",CultureInfo.InvariantCulture));
                var linked=link.GetLinkDocument();
                if(linked==null){text.Append("|unloaded");continue;}
                using var version=Document.GetDocumentVersion(linked);
                text.Append('|').Append(DocumentSession.Key(linked)).Append('|').Append(version.VersionGUID).Append('|').Append(version.NumberOfSaves)
                    .Append('|').Append(ScanCoordinator.DocumentRevision(DocumentSession.Key(linked)));
            }
            using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-","");
        }
    }
    internal static class LiveClashResolver
    {
        private static LiveElementIdentity CaptureIdentity(Document host,Element element,string linkId) {
            if(element==null||!element.IsValidObject)throw new InvalidOperationException("Result endpoint is no longer valid");
            var source=linkId==""?host:(host.GetElement(linkId) as RevitLinkInstance)?.GetLinkDocument();
            if(source==null || source.GetElement(element.UniqueId)?.Id.Value!=element.Id.Value)throw new InvalidOperationException("Linked result identity changed");
            return new LiveElementIdentity(element.Id.Value,element.UniqueId,DocumentSession.Key(source),linkId,element.VersionGuid.ToString());
        }
        public static LiveClashDto Capture(Document host,ClashResult row,string environmentStamp) => new LiveClashDto(row,
            CaptureIdentity(host,row.ElementA,row.LinkInstanceA),CaptureIdentity(host,row.ElementB,row.LinkInstanceB),
            new LivePoint(row.ClashPoint.X,row.ClashPoint.Y,row.ClashPoint.Z),environmentStamp);
        private static Element ResolveEndpoint(Document host,LiveElementIdentity endpoint) {
            var source=endpoint.LinkInstanceUniqueId==""?host:(host.GetElement(endpoint.LinkInstanceUniqueId) as RevitLinkInstance)?.GetLinkDocument();
            if(source==null||!source.IsValidObject||DocumentSession.Key(source)!=endpoint.DocumentKey)
                throw new InvalidOperationException("Linked model is unavailable or has been replaced; reload it and re-check in Radar");
            var element=source.GetElement(endpoint.UniqueId);
            if(element==null||!element.IsValidObject||element.Id.Value!=endpoint.ElementId||element.VersionGuid.ToString()!=endpoint.VersionGuid)
                throw new InvalidOperationException("Element identity or geometry changed; re-check before using this result");
            return element;
        }
        public static bool IsCurrent(Document host,LiveClashDto row) {
            try{Resolve(host,row);return true;}catch(InvalidOperationException){return false;}
        }
        public static ClashResult Resolve(Document host,LiveClashDto row) {
            if(DocumentSession.Key(host)!=row.HostDocumentKey||ScanCoordinator.IsStale(row)||row.Verification!=LiveVerificationState.Verified)
                throw new InvalidOperationException("Result is stale or unverified; re-check before navigation or inspection");
            if(LiveEnvironment.Capture(host)!=row.EnvironmentStamp)throw new InvalidOperationException("Rules or linked-model inputs changed; re-check in Radar");
            var a=ResolveEndpoint(host,row.EndpointA);var b=ResolveEndpoint(host,row.EndpointB);
            return new ClashResult {ElementA=a,ElementB=b,HostDocumentKey=row.HostDocumentKey,SessionGeneration=row.SessionGeneration,
                GeometryRevision=row.GeometryRevision,LiveSessionId=row.LiveSessionId,Origin=row.Origin,ClashId=row.ClashId,
                LinkInstanceA=row.LinkInstanceA,LinkInstanceB=row.LinkInstanceB,ClashPoint=new XYZ(row.ClashPoint.X,row.ClashPoint.Y,row.ClashPoint.Z),
                DisciplineA=row.DisciplineA,DisciplineB=row.DisciplineB,TestType=row.TestType,Severity=row.Severity,GapMM=row.GapMM,
                OverlapVolumeMM3=row.OverlapVolumeMM3,GeometryEvidence=row.GeometryEvidence,RequiredClearanceMM=row.RequiredClearanceMM,
                LevelName=row.LevelName,GridRef=row.GridRef,LocationText=row.LocationText,LinkFileA=row.LinkFileA,LinkFileB=row.LinkFileB};
        }
    }
}
