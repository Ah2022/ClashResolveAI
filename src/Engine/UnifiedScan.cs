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
        private ScanPlan? _plan;
        private Dictionary<string,BIMEngine.Core.Spatial.AABB>? _areaBounds;
        private RevitElementClassifier? _classifier;
        private bool _ownsCategoryFilter;
        private CompiledPairPolicy? _pairPolicy;
        private ScanSpatialScope? _spatialScope;
        private sealed class PartitionIndex
        {
            public ClassifiedPartition Descriptor=null!;
            public readonly List<IndexedElement> Entries=new List<IndexedElement>();
            public BIMEngine.Core.Spatial.Octree Index=null!;
            public readonly Dictionary<long,IndexedElement> ById=new Dictionary<long,IndexedElement>();
        }
        private readonly Dictionary<string,List<PartitionIndex>> _partitionIndexes=new Dictionary<string,List<PartitionIndex>>();
        private readonly Dictionary<string,Tuple<double,double>> _levelBands=new Dictionary<string,Tuple<double,double>>();
        private readonly Dictionary<string,Tuple<double,double>> _namedLevelBands=new Dictionary<string,Tuple<double,double>>();
        private readonly Dictionary<string,List<IndexedElement>> _preparedModels=new Dictionary<string,List<IndexedElement>>();
        private BoundingBoxXYZ? _scopeEnvelope;
        private sealed class IndexedElement
        {
            public Element Element = null!;
            public BoundingBoxXYZ Box = null!;
            public ElementClassification? Classification;
        }
        private readonly Dictionary<string, List<Element>> _elements = new Dictionary<string, List<Element>>();
        private static string IndexKey(LinkedModel? link) => link?.Instance.UniqueId ?? "host";
        private static BIMEngine.Core.Spatial.AABB Bounds(BoundingBoxXYZ b) => new BIMEngine.Core.Spatial.AABB(b.Min.X,b.Min.Y,b.Min.Z,b.Max.X,b.Max.Y,b.Max.Z);
        private IEnumerable<int> PrepareIndex(Document doc,LinkedModel? link,AppSettings settings,ScanStatistics stats)
        {
            stats.Phase="Snapshot";
            var elements=Collect(doc,settings);
            var prepared=new List<IndexedElement>();_preparedModels[IndexKey(link)]=prepared;
            foreach(var element in elements)
            {
                yield return 0;
                var classification=Classify(element,stats);
                if(!ScanPlanEligibility.Includes(_plan!,classification!)){stats.ExcludedBeforeIndex++;continue;}
                var box=LinkedModelManager.GetTransformedBB(element,link?.Transform??Transform.Identity);
                if(box==null)continue;
                prepared.Add(new IndexedElement {Element=element,Box=box,Classification=classification});
            }
        }
        private IEnumerable<int> BuildIndex(Document doc, LinkedModel? link, AppSettings settings, ScanStatistics stats)
        {
            var eligible=new List<Element>();_elements[IndexKey(link)]=eligible;
            var partitions=new Dictionary<string,PartitionIndex>();
            foreach(var entry in _preparedModels[IndexKey(link)]) {
                yield return 0;
                var element=entry.Element;var box=entry.Box;var classification=entry.Classification!;
                if(!IndexScope(box)){stats.ExcludedByScopeBeforeIndex++;continue;}
                eligible.Add(element); stats.Scope.NoteEligible(element.Id.Value,link?.Instance.UniqueId ?? "");
                if(_areaBounds!=null)_areaBounds[(link?.Instance.UniqueId??"")+":"+element.Id.Value]=Bounds(box);
                var descriptor=new ClassifiedPartition(classification!);
                if(!partitions.TryGetValue(descriptor.Key,out var partition))partitions[descriptor.Key]=partition=new PartitionIndex {Descriptor=descriptor};
                partition.Entries.Add(entry);stats.IndexedElements++;
                stats.Scope.NotePartition(element.Id.Value,link?.Instance.UniqueId??"",descriptor.Key);
            }
            stats.Phase="Index";
            foreach(var partition in partitions.Values) {
                var boxes=partition.Entries.Select(e=>e.Box).ToList();
                partition.Index=new BIMEngine.Core.Spatial.Octree(new BIMEngine.Core.Spatial.AABB(
                    boxes.Min(b=>b.Min.X),boxes.Min(b=>b.Min.Y),boxes.Min(b=>b.Min.Z),boxes.Max(b=>b.Max.X),boxes.Max(b=>b.Max.Y),boxes.Max(b=>b.Max.Z)).Expand(.5));
                foreach(var entry in partition.Entries) {
                    var snapshot=Snapshot(entry.Element,link,entry.Box);
                    _lookup[snapshot.ScanKey]=entry;partition.ById[entry.Element.Id.Value]=entry;partition.Index.Insert(snapshot.ToSpatialEntry());yield return 0;
                }
            }
            _partitionIndexes[IndexKey(link)]=partitions.Values.OrderBy(p=>p.Descriptor.Key,StringComparer.Ordinal).ToList();
            _preparedModels.Remove(IndexKey(link));
        }
        private bool IndexScope(BoundingBoxXYZ box)
        {
            if(_plan==null||_plan.WholeModelScope)return true;
            return _scopeEnvelope!=null&&AABBOverlap(box,_scopeEnvelope);
        }
        private bool PlannedSourceScope(Element source,LinkedModel? sourceLink,LinkedModel? targetLink,BoundingBoxXYZ? box=null)
        {
            var bounds=box??ElementBox(source,sourceLink);if(bounds==null)return false;
            string workset=_plan!.Zones.Any(z=>z.WorksetName!="")?source.Document.GetWorksetTable().GetWorkset(source.WorksetId)?.Name??"":"";
            return _spatialScope!.Includes(Bounds(bounds),sourceLink?.FileName??"",targetLink?.FileName??"",workset);
        }
        private IEnumerable<IndexedElement> Candidates(BoundingBoxXYZ box,Document doc,LinkedModel? link,PartitionIndex partition,ScanStatistics stats)
        {
            stats.SpatialQueries++;
            if(!stats.NativeLinkQueries || link==null) {
                foreach(var entry in partition.Index.Query(Bounds(box)))yield return _lookup[entry.ElementId];
                yield break;
            }
            // The verification alternative uses only the already classified, allowed partition IDs.
            var local=TransformBox(box,link.Transform.Inverse);
            using var collector=new FilteredElementCollector(doc,partition.Entries.Select(e=>e.Element.Id).ToList());
            foreach(var id in collector.WherePasses(new BoundingBoxIntersectsFilter(new Outline(local.Min,local.Max))).ToElementIds()) {
                yield return partition.ById[id.Value];
            }
        }
        private IEnumerable<IndexedElement> Candidates(BoundingBoxXYZ box, Document doc, LinkedModel? link, AppSettings settings)
        {
            // Small live batches use Revit's native bounding-box index.
            var local=TransformBox(box,(link?.Transform??Transform.Identity).Inverse);
            using var collector=new FilteredElementCollector(doc);
            var ids=collector.WherePasses(CategoryFilter(settings)).WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(new Outline(local.Min,local.Max))).ToElementIds();
            foreach(var id in ids)
            {
                var element=doc.GetElement(id);
                if(element==null||!element.IsValidObject)continue;
                var classification=Classify(element,LastStatistics);
                if(classification!=null && !classification.IsCoordinationCandidate)continue;
                var bounds=LinkedModelManager.GetTransformedBB(element,link?.Transform??Transform.Identity);
                if(bounds!=null){LastStatistics.Scope.NoteEligible(element.Id.Value,link?.Instance.UniqueId ?? "");yield return new IndexedElement{Element=element,Box=bounds,Classification=classification};}
            }
        }
        public ScanJob CreateJob(IEnumerable<ElementId>? changedIds, bool includeLinks, CoordinationZone? zone=null, string levelId="", ScanMode? mode=null, bool? includeLinkToLink=null, bool nativeLinkQueries=false,AppSettings? settingsSnapshot=null)
        {
            if(changedIds==null) {
                _links.InvalidateCache();
                var plan=RevitScanPlanFactory.Create(_doc,(settingsSnapshot ?? AppSettings.Load()).ScanSnapshot(),_links.GetLoadedLinks(),includeLinks,levelId,zone,mode,includeLinkToLink);
                return CreatePlannedJob(plan,nativeLinkQueries);
            }
            _hostKey=DocumentSession.Key(_doc);
            ClearJobCaches();
            _links.InvalidateCache(); // Capture current loaded documents and transforms for this job.
            var frozenSettings=(settingsSnapshot??AppSettings.Load()).ScanSnapshot();
            var effectiveMode=mode??frozenSettings.LiveMode;
            var stats=new ScanStatistics { Scope=new ScanScope(_hostKey),Mode=effectiveMode }; LastStatistics=stats;
            var results=new List<ClashResult>();
            return new ScanJob(EnumerateTargeted(changedIds.ToList(),includeLinks,zone,levelId,results,stats,mode,nativeLinkQueries,frozenSettings),results,stats,ClearJobCaches);
        }
        public ScanJob CreatePlannedJob(ScanPlan plan, bool nativeLinkQueries=false)
        {
            ScanProfiles.Validate(plan);
            if(plan.DocumentKey!=DocumentSession.Key(_doc))throw new ArgumentException("Scan plan belongs to a different project.",nameof(plan));
            if(plan.Relationships().Count==0)throw new ArgumentException("Select at least one scan relationship.",nameof(plan));
            if(string.IsNullOrWhiteSpace(plan.RuleSetContent))throw new ArgumentException("A scan plan must contain a frozen rule set.",nameof(plan));
            if(plan.Stage==ScanStage.InsulationClearance && plan.Mode!=ScanMode.HardAndClearance)
                throw new ArgumentException("Insulation requires a clearance scan.",nameof(plan));
            if(plan.Zones.Any(z=>z.GridRangeMin!="" || z.GridRangeMax!=""))
                throw new NotSupportedException("Grid-range zone boundaries are not supported by this scan engine.");
            if(plan.LevelIds.Any(id=>!long.TryParse(id,out var value) || !(_doc.GetElement(new ElementId(value)) is Level)))
                throw new ArgumentException("A selected floor no longer exists. Review the scan scope.",nameof(plan));
            if(plan.Zones.Any(z=>z.LevelName!="" && !GetSortedLevels().Any(l=>l.Name==z.LevelName)))
                throw new ArgumentException("A selected zone floor no longer exists. Review the scan scope.",nameof(plan));
            ClearJobCaches(); _hostKey=plan.DocumentKey; _plan=plan; _classifier=new RevitElementClassifier(plan);
            var levels=GetSortedLevels();
            _spatialScope=new ScanSpatialScope(plan,levels.Select(l=>new ScanFloor(l.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),l.Name,l.Elevation)));
            foreach(var level in levels) {
                var band=Tuple.Create(level.Elevation,levels.FirstOrDefault(l=>l.Elevation>level.Elevation+.1)?.Elevation??level.Elevation+16.4);
                _levelBands[level.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]=band;_namedLevelBands[level.Name]=band;
            }
            _links.InvalidateCache();
            var available=RevitScanPlanFactory.Links(_doc).Where(l=>l.IsLoaded).ToList();
            if(plan.SelectedLinkInstanceIds.Any(id=>!available.Any(l=>l.Instance.UniqueId==id)))
                throw new InvalidOperationException("A selected link is unloaded or unavailable. Review the scan plan.");
            var settings=new AppSettings {RuleSetName=plan.RuleSetName,EffectiveMode=plan.Mode,FullScanMode=plan.Mode,
                IncludeStructural=plan.CandidatePolicy.IncludeStructural,IncludeGenericModels=plan.CandidatePolicy.IncludeGenericModels,
                IncludeInsulation=plan.CandidatePolicy.IncludeInsulation && plan.Stage==ScanStage.InsulationClearance,
                ExcludeNamedSupports=plan.CandidatePolicy.ExcludeNamedSupports,ExcludeConnectedJoints=plan.ExcludeConnectedJoints,
                MinimumOverlapMM3=plan.MinimumOverlapMM3,InsulationMM=plan.InsulationMM,MaintenanceMM=plan.MaintenanceMM};
            _rules=ClashResolveAI.Rules.RulesEngine.FromSnapshot(plan.RuleSetContent);
            _pairPolicy=new CompiledPairPolicy(plan);
            var stats=new ScanStatistics {Scope=new ScanScope(_hostKey),Mode=plan.Mode,ScanPlanJson=Newtonsoft.Json.JsonConvert.SerializeObject(plan),
                IncludeLinkToLink=plan.LinkToLink && plan.LinkPairs.Count>0,NativeLinkQueries=nativeLinkQueries && !plan.InsideLinks && !(plan.LinkToLink && plan.LinkPairs.Count>0),
                ProjectMappingsFingerprint=plan.ProjectMappingsFingerprint,
                CoversWholeHostScope=plan.WholeModelScope && plan.HostToHost && plan.ExcludedRelationships.Count==0 && plan.Disciplines.Count==0 && plan.DisciplinePairs.Count==0 && plan.CategoryIds.Count==0 && plan.ClearanceRuleKeys.Count==0 && (available.Count==0 ||
                    (plan.HostToLinks && available.All(l=>plan.SelectedLinkInstanceIds.Contains(l.Instance.UniqueId))))};
            LastStatistics=stats; stats.Scope.RestrictToEligibleElements();stats.Scope.RestrictToPartitions();
            if(plan.CoordinationAreas.Count>0) {
                var bounds=new Dictionary<string,BIMEngine.Core.Spatial.AABB>();_areaBounds=bounds;
                stats.Scope.RestrictPairs((a,la,b,lb)=>bounds.TryGetValue(la+":"+a,out var ba)&&bounds.TryGetValue(lb+":"+b,out var bb)&&ScanStaging.AllowsPair(plan,ba,bb),new {Areas=plan.CoordinationAreas,ElementBounds=bounds});
            }
            stats.ScanCaptureJson=Newtonsoft.Json.JsonConvert.SerializeObject(Dashboard.Persistence.RevitScanCapture.Capture(_doc,settings,
                plan.SelectedLinkInstanceIds.Count>0,"",null,plan.Mode,stats.IncludeLinkToLink,plan));
            var results=new List<ClashResult>();
            return new ScanJob(EnumeratePlan(plan,available,settings,results,stats),results,stats,ClearJobCaches);
        }
        private ElementClassification? Classify(Element element,ScanStatistics stats)
        {
            if(_classifier==null)return null;
            int before=_classifier.ClassificationCount;
            var result=_classifier.Get(element);
            stats.ClassifiedElements=_classifier.ClassificationCount;
            if(before!=_classifier.ClassificationCount && result.Discipline==Discipline.Unknown)stats.UnclassifiedElements++;
            return result;
        }
        private IEnumerable<int> EnumeratePlan(ScanPlan plan,List<LinkedModel> loaded,AppSettings settings,List<ClashResult> results,ScanStatistics stats)
        {
            var relationships=plan.Relationships();
            var needed=new HashSet<string>(relationships.SelectMany(p=>new[]{p.Source,p.Target}));
            var models=loaded.Where(l=>needed.Contains(l.Instance.UniqueId)).ToDictionary(l=>l.Instance.UniqueId);
            LinkedModel? Link(string id) => id==""?null:models[id];
            Document Doc(string id) => id==""?_doc:models[id].LinkDoc;
            foreach(string id in needed.OrderBy(k=>k,StringComparer.Ordinal))
                foreach(var step in ScanStatistics.Timed(PrepareIndex(Doc(id),Link(id),settings,stats),ms=>stats.CollectIndexMilliseconds+=ms))yield return step;
            if(!plan.WholeModelScope) {
                // A source can cross a floor boundary by many metres. Use its complete bounds,
                // rather than clipping targets to the floor band and losing valid boundary clashes.
                foreach(var pair in relationships)foreach(var entry in _preparedModels[IndexKey(Link(pair.Source))]) {
                    yield return 0;
                    if(!PlannedSourceScope(entry.Element,Link(pair.Source),Link(pair.Target),entry.Box))continue;
                    var b=entry.Box;
                    _scopeEnvelope=_scopeEnvelope==null?new BoundingBoxXYZ {Min=b.Min,Max=b.Max}:new BoundingBoxXYZ {
                        Min=new XYZ(Math.Min(_scopeEnvelope.Min.X,b.Min.X),Math.Min(_scopeEnvelope.Min.Y,b.Min.Y),Math.Min(_scopeEnvelope.Min.Z,b.Min.Z)),
                        Max=new XYZ(Math.Max(_scopeEnvelope.Max.X,b.Max.X),Math.Max(_scopeEnvelope.Max.Y,b.Max.Y),Math.Max(_scopeEnvelope.Max.Z,b.Max.Z))};
                }
                if(_scopeEnvelope!=null)_scopeEnvelope=Expand(_scopeEnvelope,plan.Mode==ScanMode.HardOnly?1/304.8:
                    Math.Max(_rules.MaximumClearanceFt,Math.Max(plan.InsulationMM,plan.MaintenanceMM)/304.8));
            }
            foreach(string id in needed.OrderBy(k=>k,StringComparer.Ordinal)) {
                foreach(var step in ScanStatistics.Timed(BuildIndex(Doc(id),Link(id),settings,stats),ms=>stats.CollectIndexMilliseconds+=ms))yield return step;
            }
            stats.TotalSources=relationships.Sum(p=>_elements[IndexKey(Link(p.Source))].Count);
            var seen=new HashSet<string>();
            foreach(var pair in relationships) {
                var sourceLink=Link(pair.Source); var targetLink=Link(pair.Target);
                stats.CurrentRelationship=(sourceLink?.FileName??"Host")+" ↔ "+(targetLink?.FileName??"Host");
                stats.RelationshipCompletedSources=0;stats.RelationshipTotalSources=_elements[IndexKey(sourceLink)].Count;
                foreach(var source in _elements[IndexKey(sourceLink)]) {
                    stats.RelationshipCompletedSources++;
                    stats.CompletedSources++; yield return 0;
                    if(!PlannedSourceScope(source,sourceLink,targetLink)){stats.Excluded++;continue;}
                    var sourceBox=ElementBox(source,sourceLink)!;
                    stats.CurrentFloor=GetSortedLevels().LastOrDefault(l=>l.Elevation<=sourceBox.Min.Z+.1)?.Name??"Below first level";
                    stats.Sources++;
                    foreach(var step in ScanSource(source,sourceLink,Doc(pair.Target),targetLink,results,seen,settings,stats))yield return step;
                }
            }
            stats.Phase="Publish";
        }
        private void ClearJobCaches()
        {
            _solidCache?.Dispose();_solidCache=null;
            if(_ownsCategoryFilter)_categoryFilter?.Dispose();
            _categoryFilter=null; _ownsCategoryFilter=false;
            _plan=null; _classifier=null;
            _areaBounds=null;
            _pairPolicy=null;_partitionIndexes.Clear();
            _spatialScope=null;
            _levelBands.Clear();_namedLevelBands.Clear();
            _preparedModels.Clear();_scopeEnvelope=null;
            _geometry.Clear();_snapshots.Clear();_lookup.Clear();_elements.Clear();
        }
        public List<ClashResult> ScanUnified(IEnumerable<ElementId>? changedIds,bool includeLinks,
            IProgress<string>? progress=null,CoordinationZone? zone=null,string levelId="",ScanMode? mode=null)
        {
            using var job=CreateJob(changedIds,includeLinks,zone,levelId,mode);
            while(!job.Advance(25)) progress?.Report(job.Statistics.Summary);
            foreach(var c in job.Results)c.GeometryRevision=ScanCoordinator.Revision;
            return job.Results;
        }
        private IEnumerable<int> EnumerateTargeted(List<ElementId> changed,bool includeLinks,CoordinationZone? zone,string levelId,List<ClashResult> results,ScanStatistics stats,ScanMode? mode,bool nativeLinkQueries,AppSettings frozenSettings)
        {
            var settings=frozenSettings.ScanSnapshot();
            settings.EffectiveMode=mode??settings.LiveMode;
            stats.Mode=settings.EffectiveMode;
            stats.IncludeLinkToLink=false;
            // Native link queries are an opt-in verification experiment until production evidence supports adoption.
            stats.NativeLinkQueries=nativeLinkQueries;
            _rules=ScanSessionCache.Rules(_doc,_ruleSetName);
            long collectStarted=System.Diagnostics.Stopwatch.GetTimestamp();
            var links=includeLinks?_links.GetLoadedLinks():new List<LinkedModel>();
            stats.CollectIndexMilliseconds+=ScanStatistics.Since(collectStarted);
            var seen=new HashSet<string>();
            // DocumentChanged also includes system containers, views and other
            // nonphysical dependants. Apply the same category scope as full scans.
            collectStarted=System.Diagnostics.Stopwatch.GetTimestamp();
            var sourceFilter=CategoryFilter(settings);
            var sources=changed.Select(_doc.GetElement)
                .Where(e=>e!=null&&e.IsValidObject&&!(e is ElementType)&&sourceFilter.PassesFilter(e)).ToList();
            stats.TotalSources=sources.Count;
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
            stats.Phase="Publish";
        }
        private BoundingBoxXYZ? ElementBox(Element element,LinkedModel? link)
        {
            if(_snapshots.TryGetValue(IndexKey(link)+":"+element.Id.Value,out var snapshot)&&_lookup.TryGetValue(snapshot.ScanKey,out var entry))return entry.Box;
            return LinkedModelManager.GetTransformedBB(element,link?.Transform??Transform.Identity);
        }
        private bool InScope(Element e,LinkedModel? link,CoordinationZone? zone,string levelId,BoundingBoxXYZ? suppliedBox=null)
        {
            if(e==null||!e.IsValidObject)return false;
            var box=suppliedBox??ElementBox(e,link);
            if(box==null)return false;
            if(_plan!=null) {
                Tuple<double,double>? band=!string.IsNullOrEmpty(levelId)?_levelBands[levelId]:
                    zone!=null&&!string.IsNullOrEmpty(zone.LevelName)?_namedLevelBands[zone.LevelName]:null;
                if(band!=null&&(box.Max.Z<band.Item1||box.Min.Z>=band.Item2))return false;
                return zone==null||string.IsNullOrEmpty(zone.WorksetName)||e.Document.GetWorksetTable().GetWorkset(e.WorksetId)?.Name==zone.WorksetName;
            }
            var level=!string.IsNullOrEmpty(levelId)?_doc.GetElement(new ElementId(long.Parse(levelId))) as Level:null;
            if(level==null&&zone!=null&&!string.IsNullOrEmpty(zone.LevelName))level=GetSortedLevels().FirstOrDefault(l=>l.Name==zone.LevelName);
            if(level!=null&&(box.Max.Z<level.Elevation||box.Min.Z>=GetNextLevelElevation(level.Elevation)))return false;
            if(zone!=null&&!string.IsNullOrEmpty(zone.WorksetName)&&e.Document.GetWorksetTable().GetWorkset(e.WorksetId)?.Name!=zone.WorksetName)return false;
            return true;
        }
        private ElementMulticategoryFilter CategoryFilter(AppSettings s)
        {
            if(_categoryFilter!=null)return _categoryFilter;
            if(_plan==null)return _categoryFilter=ScanSessionCache.Filter(_doc,s);
            var cats=Enum.GetValues(typeof(Discipline)).Cast<Discipline>().SelectMany(ElementCollector.GetCategoriesForDiscipline).Distinct().ToList();
            var envelopes=new[]{BuiltInCategory.OST_Walls,BuiltInCategory.OST_Floors,BuiltInCategory.OST_Ceilings};
            cats.RemoveAll(c=>envelopes.Contains(c) && !_plan.CandidatePolicy.RequiredEnvelopeCategoryIds.Contains((long)c));
            if(!s.IncludeStructural)cats.RemoveAll(c=>ElementCollector.GetCategoriesForDiscipline(Discipline.Structural).Contains(c));
            if(s.IncludeGenericModels){cats.Add(BuiltInCategory.OST_GenericModel);cats.Add(BuiltInCategory.OST_SpecialityEquipment);}
            if(s.IncludeInsulation){cats.Add(BuiltInCategory.OST_PipeInsulations);cats.Add(BuiltInCategory.OST_DuctInsulations);cats.Add(BuiltInCategory.OST_DuctLinings);}
            if(_plan.CategoryIds.Count>0)cats.RemoveAll(c=>!_plan.CategoryIds.Contains((long)c));
            _ownsCategoryFilter=true;
            return _categoryFilter=new ElementMulticategoryFilter(cats);
        }
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
            if(_plan!=null) {
                foreach(var step in ScanPlannedSource(source,sl,targetDoc,tl,results,seen,settings,stats))yield return step;
                yield break;
            }
            if(!source.IsValidObject||source.Category==null||!targetDoc.IsValidObject)yield break;
            var sourceClassification=Classify(source,stats);
            if(sourceClassification!=null && !sourceClassification.IsCoordinationCandidate)yield break;
            var da=sourceClassification?.Discipline ?? ElementCollector.GetDiscipline(source);
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
                var db=candidate.Classification?.Discipline ?? ElementCollector.GetDiscipline(target);
                string ruleKey=GetRuleKey(da,db);
                var rule=_rules.ResolvePair(source,target,da,db,ruleKey);
                if((!settings.IncludeStructural&&db==Discipline.Structural)||(da==Discipline.Structural&&db==Discipline.Structural)||
                    (sl==tl&&((source is InsulationLiningBase ia&&ia.HostElementId==target.Id)||(target is InsulationLiningBase ib&&ib.HostElementId==source.Id)))||rule.Ignore||(_plan==null&&settings.ExcludeNamedSupports&&(ElementCollector.IsSupportOrAccessoryFamily(source)||ElementCollector.IsSupportOrAccessoryFamily(target)))||
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
        private IEnumerable<int> ScanPlannedSource(Element source,LinkedModel? sl,Document targetDoc,LinkedModel? tl,List<ClashResult> results,HashSet<string> seen,AppSettings settings,ScanStatistics stats)
        {
            if(!source.IsValidObject||!targetDoc.IsValidObject)yield break;
            var classification=Classify(source,stats)!;
            if(!classification.IsCoordinationCandidate)yield break;
            var sourcePart=new ClassifiedPartition(classification);
            var a=ElementBox(source,sl);if(a==null)yield break;
            string sourceLink=sl?.Instance.UniqueId??"",targetLink=tl?.Instance.UniqueId??"";
            stats.Scope.Note(source.Id.Value,sourceLink,targetLink);
            foreach(var partition in _partitionIndexes[IndexKey(tl)]) {
                stats.PartitionRuleChecks++;
                var decision=_pairPolicy!.Resolve(sourcePart,partition.Descriptor);
                if(!decision.Allowed) {
                    stats.RejectedPartitionChecks++;stats.ExcludedRelationshipsBeforeSpatial+=partition.Entries.Count;
                    yield return 0;continue;
                }
                stats.Scope.AllowTargetPartition(sourceLink,targetLink,sourcePart.Key,partition.Descriptor.Key,decision.ClearanceEnabled);
                bool hardOnly=!decision.ClearanceEnabled;
                double clearance=Math.Max(decision.ClearanceMM,settings.InsulationMM)/304.8;
                double threshold=Math.Max(clearance,settings.MaintenanceMM/304.8);
                double radius=hardOnly?1/304.8:threshold;
                var rule=decision.CopyRule();stats.Phase="Filter";
                foreach(var candidate in ScanStatistics.Timed(Candidates(Expand(a,radius),targetDoc,tl,partition,stats),ms=>stats.CandidateMilliseconds+=ms)) {
                    yield return 0;
                    var target=candidate.Element;
                    if(sl==tl&&source.Id==target.Id)continue;
                    if(!ScanStaging.AllowsPair(_plan!,Bounds(a),Bounds(candidate.Box))){stats.ExcludedByArea++;continue;}
                    string key=ClashIdentity.Pair(_hostKey,source.Id.Value,sourceLink,target.Id.Value,targetLink);
                    if(!seen.Add(key))continue;
                    stats.Candidates++;
                    if((sl==tl&&((source is InsulationLiningBase ia&&ia.HostElementId==target.Id)||(target is InsulationLiningBase ib&&ib.HostElementId==source.Id)))||
                        (settings.ExcludeConnectedJoints&&sl==tl&&ElementCollector.AreSameConnectedSystem(source,target))) {stats.Excluded++;continue;}
                    if(hardOnly?!AABBOverlap(a,candidate.Box):BoxDistance(a,candidate.Box)>threshold) {if(hardOnly)stats.SkippedClearance++;continue;}
                    foreach(var step in VerifyPair(source,sl,target,tl,classification.Discipline,partition.Descriptor.Discipline,decision.RuleKey,rule,key,
                        a,candidate.Box,clearance,threshold,results,settings,stats,decision.RuleId,hardOnly))yield return step;
                }
            }
        }
        private IEnumerable<int> VerifyPair(Element source,LinkedModel? sl,Element target,LinkedModel? tl,Discipline da,Discipline db,string ruleKey,ClashResolveAI.Rules.ClashRule rule,string key,BoundingBoxXYZ a,BoundingBoxXYZ b,double clearance,double threshold,List<ClashResult> results,AppSettings settings,ScanStatistics stats,string ruleId="",bool? hardOnlyOverride=null)
        {
            stats.Phase="Verify";
                bool hardOnly=hardOnlyOverride??settings.EffectiveMode==ScanMode.HardOnly;
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
                clash.ElementUniqueIdA=clash.ElementA.UniqueId;clash.ElementUniqueIdB=clash.ElementB.UniqueId;
                clash.ElementSignatureA=clash.ElementA.VersionGuid.ToString();clash.ElementSignatureB=clash.ElementB.VersionGuid.ToString();
                clash.RuleApplied=$"{_rules.CurrentRuleSetName}: {(ruleId==""?ruleKey:ruleId)}";clash.Priority=hard?(severity==ClashSeverity.Critical?"Critical":"High"):unknown?"Review":"Medium";
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
