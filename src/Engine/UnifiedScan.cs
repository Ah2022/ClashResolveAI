using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using ClashResolveAI.Links;
using ClashResolveAI.LiveMonitor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ClashResolveAI.ClashEngine
{
    public partial class ClashEngine
    {
        private sealed class GeometryData { public List<Solid> Solids = new List<Solid>(); public string Error = ""; public SurfaceDistance.Prepared? Surface; }
        private BIMEngine.Core.Geometry.SolidCache? _solidCache;
        private readonly Dictionary<string,BIMEngine.Core.Models.SpatialElement> _snapshots=new Dictionary<string,BIMEngine.Core.Models.SpatialElement>();
        private readonly Dictionary<long,IndexedElement> _lookup=new Dictionary<long,IndexedElement>();
        private BIMEngine.Core.Models.SpatialElement Snapshot(Element e,LinkedModel? link,BoundingBoxXYZ? box=null)
        {
            string key=IndexKey(link)+":"+e.Id.Value;
            if(_snapshots.TryGetValue(key,out var value))return value;
            value=new BIMEngine.Core.Models.SpatialElement { ScanKey=_snapshots.Count+1,ElementId=e.Id.Value,
                ElementUniqueId=e.UniqueId,IsFromLinkedDocument=link!=null,LinkedDocumentReferenceId=link?.Instance.UniqueId,
                Bounds=box==null?default:Bounds(box) };
            _snapshots[key]=value;return value;
        }
        private readonly Dictionary<string,GeometryData> _geometry = new Dictionary<string,GeometryData>();
        public ScanStatistics LastStatistics { get; private set; } = new ScanStatistics();
        private string _hostKey = "";
        private ElementMulticategoryFilter? _categoryFilter;
        private sealed class IndexedElement
        {
            public Element Element = null!;
            public BoundingBoxXYZ Box = null!;
        }
        private readonly Dictionary<string, BIMEngine.Core.Spatial.Octree> _indexes = new Dictionary<string, BIMEngine.Core.Spatial.Octree>();
        private readonly Dictionary<string, List<Element>> _elements = new Dictionary<string, List<Element>>();
        private static string IndexKey(LinkedModel? link) => link?.Instance.UniqueId ?? "host";
        private static BIMEngine.Core.Spatial.AABB Bounds(BoundingBoxXYZ b) => new BIMEngine.Core.Spatial.AABB(b.Min.X,b.Min.Y,b.Min.Z,b.Max.X,b.Max.Y,b.Max.Z);
        private IEnumerable<int> BuildIndex(Document doc, LinkedModel? link, AppSettings settings, ScanStatistics stats)
        {
            stats.Phase="Snapshot";
            var elements=Collect(doc,settings);
            _elements[IndexKey(link)]=elements;
            var entries=new List<IndexedElement>();
            double x=double.PositiveInfinity,y=x,z=x,X=double.NegativeInfinity,Y=X,Z=X;
            foreach(var element in elements)
            {
                yield return 0;
                var box=LinkedModelManager.GetTransformedBB(element,link?.Transform??Transform.Identity);
                if(box==null)continue;
                entries.Add(new IndexedElement{Element=element,Box=box});stats.IndexedElements++;
                x=Math.Min(x,box.Min.X);y=Math.Min(y,box.Min.Y);z=Math.Min(z,box.Min.Z);
                X=Math.Max(X,box.Max.X);Y=Math.Max(Y,box.Max.Y);Z=Math.Max(Z,box.Max.Z);
            }
            if(entries.Count==0)x=y=z=X=Y=Z=0;
            stats.Phase="Index";
            var index=new BIMEngine.Core.Spatial.Octree(new BIMEngine.Core.Spatial.AABB(x,y,z,X,Y,Z).Expand(.5));
            foreach(var entry in entries)
            {
                var snapshot=Snapshot(entry.Element,link,entry.Box);
                _lookup[snapshot.ScanKey]=entry;index.Insert(snapshot.ToSpatialEntry());yield return 0;
            }
            _indexes[IndexKey(link)]=index;
        }
        private IEnumerable<IndexedElement> Candidates(BoundingBoxXYZ box, Document doc, LinkedModel? link, AppSettings settings)
        {
            if(_indexes.TryGetValue(IndexKey(link),out var index))
            {
                foreach(var entry in index.Query(Bounds(box)))yield return _lookup[entry.ElementId];
                yield break;
            }
            // Small live batches use Revit's native bounding-box index.
            var local=TransformBox(box,(link?.Transform??Transform.Identity).Inverse);
            using var collector=new FilteredElementCollector(doc);
            var ids=collector.WherePasses(CategoryFilter(settings)).WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(new Outline(local.Min,local.Max))).ToElementIds();
            foreach(var id in ids)
            {
                var element=doc.GetElement(id);
                if(element==null||!element.IsValidObject)continue;
                var bounds=LinkedModelManager.GetTransformedBB(element,link?.Transform??Transform.Identity);
                if(bounds!=null)yield return new IndexedElement{Element=element,Box=bounds};
            }
        }
        public ScanJob CreateJob(IEnumerable<ElementId>? changedIds, bool includeLinks, CoordinationZone? zone=null, string levelId="", ScanMode? mode=null, bool? includeLinkToLink=null, bool nativeLinkQueries=false,AppSettings? settingsSnapshot=null)
        {
            _hostKey=DocumentSession.Key(_doc);
            ClearJobCaches();
            _links.InvalidateCache(); // Capture current loaded documents and transforms for this job.
            var stats=new ScanStatistics { Scope=new ScanScope(_hostKey) }; LastStatistics=stats;
            var results=new List<ClashResult>();
            return new ScanJob(Enumerate(changedIds?.ToList(),includeLinks,zone,levelId,results,stats,mode,includeLinkToLink,nativeLinkQueries,settingsSnapshot??AppSettings.Load().ScanSnapshot()),results,stats,ClearJobCaches);
        }
        private void ClearJobCaches()
        {
            _solidCache?.Dispose();_solidCache=null;
            _categoryFilter=null;
            _geometry.Clear();_snapshots.Clear();_lookup.Clear();_indexes.Clear();_elements.Clear();
        }
        public List<ClashResult> ScanUnified(IEnumerable<ElementId>? changedIds,bool includeLinks,
            IProgress<string>? progress=null,CoordinationZone? zone=null,string levelId="",ScanMode? mode=null)
        {
            using var job=CreateJob(changedIds,includeLinks,zone,levelId,mode);
            while(!job.Advance(25)) progress?.Report(job.Statistics.Summary);
            foreach(var c in job.Results)c.GeometryRevision=ScanCoordinator.Revision;
            return job.Results;
        }
        private IEnumerable<int> Enumerate(List<ElementId>? changed,bool includeLinks,CoordinationZone? zone,string levelId,List<ClashResult> results,ScanStatistics stats,ScanMode? mode,bool? includeLinkToLink,bool nativeLinkQueries,AppSettings frozenSettings)
        {
            var settings=frozenSettings.ScanSnapshot();
            settings.EffectiveMode=mode??(changed==null?settings.FullScanMode:settings.LiveMode);
            stats.Mode=settings.EffectiveMode;
            settings.IncludeLinkToLink=includeLinkToLink??settings.IncludeLinkToLink;
            bool linkedSources=changed==null&&includeLinks&&settings.IncludeLinkToLink;
            stats.IncludeLinkToLink=linkedSources;
            // Native link queries are an opt-in verification experiment until production evidence supports adoption.
            stats.NativeLinkQueries=nativeLinkQueries&&!linkedSources;
            _rules=ScanSessionCache.Rules(_doc,_ruleSetName);
            long collectStarted=System.Diagnostics.Stopwatch.GetTimestamp();
            var links=includeLinks?_links.GetLoadedLinks():new List<LinkedModel>();
            stats.CollectIndexMilliseconds+=ScanStatistics.Since(collectStarted);
            var seen=new HashSet<string>();
            if(changed==null)
            {
                foreach(var step in ScanStatistics.Timed(BuildIndex(_doc,null,settings,stats),ms=>stats.CollectIndexMilliseconds+=ms))yield return step;
                foreach(var link in stats.NativeLinkQueries?new List<LinkedModel>():links)
                    foreach(var step in ScanStatistics.Timed(BuildIndex(link.LinkDoc,link,settings,stats),ms=>stats.CollectIndexMilliseconds+=ms))yield return step;
            }
            // DocumentChanged also includes system containers, views and other
            // nonphysical dependants. Apply the same category scope as full scans.
            collectStarted=System.Diagnostics.Stopwatch.GetTimestamp();
            var sourceFilter=CategoryFilter(settings);
            var sources=changed==null?_elements["host"]:changed.Select(_doc.GetElement)
                .Where(e=>e!=null&&e.IsValidObject&&!(e is ElementType)&&sourceFilter.PassesFilter(e)).ToList();
            stats.TotalSources=sources.Count+(linkedSources?links.Sum(l=>_elements[IndexKey(l)].Count):0);
            stats.CollectIndexMilliseconds+=ScanStatistics.Since(collectStarted);
            foreach(var source in sources)
            {
                stats.CompletedSources++;
                yield return 0;
                if(!InScope(source,null,zone,levelId)){stats.Excluded++;continue;}
                stats.Sources++;
                foreach(var step in ScanSource(source,null,_doc,null,results,seen,settings,stats))yield return step;
                foreach(var link in links)
                    if(zone==null||string.IsNullOrEmpty(zone.LinkedModel)||zone.LinkedModel==link.FileName)
                        foreach(var step in ScanSource(source,null,link.LinkDoc,link,results,seen,settings,stats))yield return step;
            }
            if(linkedSources)
                for(int i=0;i<links.Count;i++)
                {
                    if(zone!=null&&!string.IsNullOrEmpty(zone.LinkedModel)&&zone.LinkedModel!=links[i].FileName)continue;
                    foreach(var source in _elements[IndexKey(links[i])])
                    {
                        stats.CompletedSources++;
                        yield return 0;
                        if(!InScope(source,links[i],zone,levelId)){stats.Excluded++;continue;}
                        stats.Sources++;
                        if(settings.ScanWithinLinks)
                            foreach(var step in ScanSource(source,links[i],links[i].LinkDoc,links[i],results,seen,settings,stats))yield return step;
                        for(int j=i+1;j<links.Count;j++)
                            foreach(var step in ScanSource(source,links[i],links[j].LinkDoc,links[j],results,seen,settings,stats))yield return step;
                    }
                }
            stats.Phase="Publish";
        }
        private BoundingBoxXYZ? ElementBox(Element element,LinkedModel? link)
        {
            if(_snapshots.TryGetValue(IndexKey(link)+":"+element.Id.Value,out var snapshot)&&_lookup.TryGetValue(snapshot.ScanKey,out var entry))return entry.Box;
            return LinkedModelManager.GetTransformedBB(element,link?.Transform??Transform.Identity);
        }
        private bool InScope(Element e,LinkedModel? link,CoordinationZone? zone,string levelId)
        {
            if(e==null||!e.IsValidObject)return false;
            var box=ElementBox(e,link);
            if(box==null)return false;
            var level=!string.IsNullOrEmpty(levelId)?_doc.GetElement(new ElementId(long.Parse(levelId))) as Level:null;
            if(level==null&&zone!=null&&!string.IsNullOrEmpty(zone.LevelName))level=GetSortedLevels().FirstOrDefault(l=>l.Name==zone.LevelName);
            if(level!=null&&(box.Max.Z<level.Elevation||box.Min.Z>=GetNextLevelElevation(level.Elevation)))return false;
            if(zone!=null&&!string.IsNullOrEmpty(zone.WorksetName)&&e.Document.GetWorksetTable().GetWorkset(e.WorksetId)?.Name!=zone.WorksetName)return false;
            return true;
        }
        private ElementMulticategoryFilter CategoryFilter(AppSettings s) => _categoryFilter??=ScanSessionCache.Filter(_doc,s);
        internal static ElementMulticategoryFilter BuildCategoryFilter(AppSettings s)
        {
            var cats=Enum.GetValues(typeof(Discipline)).Cast<Discipline>().SelectMany(ElementCollector.GetCategoriesForDiscipline).Distinct().ToList();
            if(s.IncludeGenericModels){cats.Add(BuiltInCategory.OST_GenericModel);cats.Add(BuiltInCategory.OST_SpecialityEquipment);}
            if(s.IncludeInsulation){cats.Add(BuiltInCategory.OST_PipeInsulations);cats.Add(BuiltInCategory.OST_DuctInsulations);cats.Add(BuiltInCategory.OST_DuctLinings);}
            return new ElementMulticategoryFilter(cats);
        }
        private List<Element> Collect(Document d,AppSettings s)=>new FilteredElementCollector(d).WherePasses(CategoryFilter(s)).WhereElementIsNotElementType().ToElements().ToList();
        private static double BoxDistance(BoundingBoxXYZ a,BoundingBoxXYZ b)
        {
            double x=Math.Max(0,Math.Max(a.Min.X-b.Max.X,b.Min.X-a.Max.X)),y=Math.Max(0,Math.Max(a.Min.Y-b.Max.Y,b.Min.Y-a.Max.Y)),z=Math.Max(0,Math.Max(a.Min.Z-b.Max.Z,b.Min.Z-a.Max.Z));
            return Math.Sqrt(x*x+y*y+z*z);
        }
        private GeometryData Geometry(Element e,LinkedModel? link)
        {
            string key=(link?.Instance.UniqueId??"host")+":"+e.Id.Value;
            if(_geometry.TryGetValue(key,out var cached)){LastStatistics.GeometryCacheHits++;return cached;}
            LastStatistics.GeometryLoads++;
            long started=System.Diagnostics.Stopwatch.GetTimestamp();
            try {
            _solidCache ??= new BIMEngine.Core.Geometry.SolidCache(_doc);
            var data=new GeometryData { Solids=_solidCache.GetSolids(Snapshot(e,link)) };
            if(data.Solids.Count==0)data.Error="No supported solid geometry; see geometry diagnostics";
            _geometry[key]=data;return data;
            } finally { LastStatistics.GeometryMilliseconds+=ScanStatistics.Since(started); }
        }
        private IEnumerable<int> ScanSource(Element source,LinkedModel? sl,Document targetDoc,LinkedModel? tl,List<ClashResult> results,HashSet<string> seen,AppSettings settings,ScanStatistics stats)
        {
            if(!source.IsValidObject||source.Category==null||!targetDoc.IsValidObject)yield break;
            var da=ElementCollector.GetDiscipline(source);
            if(!settings.IncludeStructural&&da==Discipline.Structural)yield break;
            var a=ElementBox(source,sl);if(a==null)yield break;
            stats.Scope.Note(source.Id.Value,sl?.Instance.UniqueId??"",tl?.Instance.UniqueId??"");
            bool hardOnly=settings.EffectiveMode==ScanMode.HardOnly;
            double radius=hardOnly?1/304.8:Math.Max(_rules.MaximumClearanceFt,(settings.InsulationMM+settings.MaintenanceMM)/304.8);
            stats.Phase="Filter";
            var checks=new List<IEnumerable<int>>(128);
            foreach(var candidate in ScanStatistics.Timed(Candidates(Expand(a,radius),targetDoc,tl,settings),ms=>stats.CandidateMilliseconds+=ms))
            {
                yield return 0;
                var target=candidate.Element;
                var id=target.Id;
                if(sl==tl&&source.Id==id)continue;
                string key=ClashIdentity.Pair(_hostKey,source.Id.Value,sl?.Instance.UniqueId??"",id.Value,tl?.Instance.UniqueId??"");
                if(!seen.Add(key))continue;
                stats.Candidates++;
                var db=ElementCollector.GetDiscipline(target);
                string ruleKey=GetRuleKey(da,db);
                var rule=_rules.ResolvePair(source,target,da,db,ruleKey);
                if((!settings.IncludeStructural&&db==Discipline.Structural)||(da==Discipline.Structural&&db==Discipline.Structural)||
                    (sl==tl&&((source is InsulationLiningBase ia&&ia.HostElementId==target.Id)||(target is InsulationLiningBase ib&&ib.HostElementId==source.Id)))||rule.Ignore||(settings.ExcludeNamedSupports&&(ElementCollector.IsSupportOrAccessoryFamily(source)||ElementCollector.IsSupportOrAccessoryFamily(target)))||
                    (settings.ExcludeConnectedJoints&&sl==tl&&ElementCollector.AreSameConnectedSystem(source,target)))
                {stats.Excluded++;continue;}
                var b=candidate.Box;
                double clearance=Math.Max(Math.Max(0,rule.ClearanceMM),settings.InsulationMM)/304.8;
                double threshold=Math.Max(clearance,settings.MaintenanceMM/304.8);
                if(hardOnly?!AABBOverlap(a,b):BoxDistance(a,b)>threshold){if(hardOnly)stats.SkippedClearance++;continue;}
                checks.Add(VerifyPair(source,sl,target,tl,da,db,ruleKey,rule,key,a,b,clearance,threshold,results,settings,stats));
                if(checks.Count>=128)
                {
                    foreach(var check in checks)foreach(var step in check)yield return step;
                    checks.Clear();stats.Phase="Filter";
                }
            }
            foreach(var check in checks)foreach(var step in check)yield return step;
        }
        private IEnumerable<int> VerifyPair(Element source,LinkedModel? sl,Element target,LinkedModel? tl,Discipline da,Discipline db,string ruleKey,ClashResolveAI.Rules.ClashRule rule,string key,BoundingBoxXYZ a,BoundingBoxXYZ b,double clearance,double threshold,List<ClashResult> results,AppSettings settings,ScanStatistics stats)
        {
            stats.Phase="Verify";
                bool hardOnly=settings.EffectiveMode==ScanMode.HardOnly;
                bool boolFailed=false;
                var ga=Geometry(source,sl);yield return 0;
                var gb=Geometry(target,tl);yield return 0;
                stats.Tested++;
                bool unknown=ga.Error!=""||gb.Error!="";
                if(unknown){stats.MissingGeometry++;stats.Scope.MissingPair(key);stats.Scope.MissingSource(source.Id.Value,sl?.Instance.UniqueId??"");stats.Scope.MissingSource(target.Id.Value,tl?.Instance.UniqueId??"");if(hardOnly)yield break;}
                var reason=unknown?UnverifiedReason.MissingGeometry:UnverifiedReason.SurfaceClearance;
                double volume=0;XYZ point=GetClashPoint(a,b);string evidence=unknown?ga.Error+" "+gb.Error:"";
                if(!unknown&&AABBOverlap(a,b))
                    foreach(var sa in ga.Solids)foreach(var sb in gb.Solids)
                    {
                        yield return 0;
                        long started=System.Diagnostics.Stopwatch.GetTimestamp();stats.BooleanTests++;
                        try {
                            using var hit=BooleanOperationsUtils.ExecuteBooleanOperation(sa,sb,BooleanOperationsType.Intersect);
                            if(hit!=null&&hit.Volume>0){volume+=hit.Volume*Math.Pow(304.8,3);point=hit.ComputeCentroid();}
                        }catch(Exception ex){boolFailed=true;unknown=true;reason=UnverifiedReason.SolidTest;stats.BooleanFailures++;stats.Scope.MissingPair(key);stats.Scope.MissingSource(source.Id.Value,sl?.Instance.UniqueId??"");stats.Scope.MissingSource(target.Id.Value,tl?.Instance.UniqueId??"");evidence="Boolean intersection failed: "+ex.Message;}
                        finally { stats.BooleanMilliseconds+=ScanStatistics.Since(started); }
                    }
                bool hard=volume>=Math.Max(0.001,settings.MinimumOverlapMM3);
                if(hardOnly&&!hard){
                    if(!boolFailed){if(volume>0)stats.BelowTolerance++;stats.SkippedClearance++;yield break;}
                    evidence="Possible hard: "+evidence;
                }
                double gap=0;
                if(!hardOnly&&!hard&&!unknown)
                {
                    stats.SurfaceDistanceCalls++;
                    var distance=new SurfaceDistance.Evidence();
                    IEnumerable<int> Measure()
                    {
                        ga.Surface??=SurfaceDistance.Prepare(ga.Solids);
                        gb.Surface??=SurfaceDistance.Prepare(gb.Solids);
                        foreach(var step in SurfaceDistance.MeasureSteps(ga.Surface,gb.Surface,threshold,distance))yield return step;
                    }
                    using(var measure=ScanStatistics.Timed(Measure(),ms=>stats.SurfaceDistanceMilliseconds+=ms).GetEnumerator())
                    {
                        while(true){
                            bool next;
                            try{next=measure.MoveNext();}catch(Exception ex){distance.Exact=false;distance.Distance=double.PositiveInfinity;distance.Method="Surface analysis failed: "+ex.Message;break;}
                            if(!next)break;
                            yield return 0;
                        }
                    }
                    gap=double.IsInfinity(distance.Distance)?0:distance.Distance*304.8;
                    evidence=distance.Method;
                    if(distance.Exact&&distance.Distance>threshold)yield break;
                    unknown=!distance.Exact&&!(distance.Distance<threshold);
                    point=double.IsInfinity(distance.Distance)?point:distance.Point;
                    if(volume>0){unknown=true;evidence="Overlap below configured volume tolerance";point=GetClashPoint(a,b);}
                }
                var severity=hard?(rule.Severity.Equals("Critical",StringComparison.OrdinalIgnoreCase)?ClashSeverity.Critical:ClashSeverity.Hard):
                    unknown?ClashSeverity.Clearance:gap<clearance*304.8?ClashSeverity.Soft:ClashSeverity.Clearance;
                var clash=BuildResult(results.Count+1,source,da,target,db,ruleKey,severity,gap,a,b,sl?.FileName??"",tl?.FileName??"",volume);
                if(clash==null)yield break;
                clash.Severity=severity;clash.TestType=hard?ClashTestType.HardClash:unknown?ClashTestType.Unverified:ClashTestType.ClearanceClash;
                clash.UnverifiedReason=reason;
                clash.GeometryEvidence=hard?"Confirmed solid intersection":evidence;clash.RequiredClearanceMM=threshold*304.8;
                clash.ClashPoint=point;clash.LocationText=$"X:{point.X*0.3048:F3}m Y:{point.Y*0.3048:F3}m Z:{point.Z*0.3048:F3}m";
                clash.LevelName=GetLevel(point);clash.GridRef=GetGrid(point);
                clash.HostDocumentKey=_hostKey;clash.LinkInstanceA=sl?.Instance.UniqueId??"";clash.LinkInstanceB=tl?.Instance.UniqueId??"";
                clash.RuleApplied=$"{_rules.CurrentRuleSetName}: {ruleKey}";clash.Priority=hard?(severity==ClashSeverity.Critical?"Critical":"High"):unknown?"Review":"Medium";
                using(var sha=SHA256.Create())clash.ClashId=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-","").Substring(0,24);
                if(unknown)stats.Unverified++;
                results.Add(clash);
        }
        private static BoundingBoxXYZ TransformBox(BoundingBoxXYZ box,Transform transform)
        {
            var points=(from x in new[]{box.Min.X,box.Max.X} from y in new[]{box.Min.Y,box.Max.Y} from z in new[]{box.Min.Z,box.Max.Z} select transform.OfPoint(new XYZ(x,y,z))).ToList();
            return new BoundingBoxXYZ{Min=new XYZ(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)),Max=new XYZ(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z))};
        }
    }
}
