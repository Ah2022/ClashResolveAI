using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;
int passed = 0;
void Check(bool condition, string description) { if (!condition) throw new Exception(description); Console.WriteLine("PASS " + description); passed++; }
string Key(string doc, long a, string la, long b, string lb) => ClashIdentity.Pair(doc,a,la,b,lb);
Check(Key("host",7,"",8,"link") == Key("host",8,"link",7,""), "Reversed element order preserves identity");
Check(Key("host",7,"A",7,"B") == Key("host",7,"B",7,"A"), "Equal IDs across links preserve symmetry");
Check(Key("host",7,"",8,"instance-1") != Key("host",7,"",8,"instance-2"), "Repeated linked instances stay distinct");
Check(Key("doc-1",7,"",8,"") != Key("doc-2",7,"",8,""), "Documents with matching IDs stay distinct");
Check(Key("host",long.MaxValue,"",8,"") != Key("host",int.MaxValue,"",8,""), "64-bit element IDs are not truncated");
Check(Key("x|y",7,"a:b",8,"") != Key("x",7,"y|a:b",8,""), "Separators in identifiers do not collide");
var a = new Aabb(0,0,0,1,1,1);
Check(AabbIntersector.Intersects(a,new Aabb(.5,.5,.5,2,2,2)), "Broad phase retains overlapping geometry");
Check(!AabbIntersector.Intersects(a,new Aabb(2,2,2,3,3,3)), "Broad phase excludes disjoint geometry");

var output = Path.Combine(Directory.GetCurrentDirectory(), "verification", "serialization");
Directory.CreateDirectory(output);
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
var issue = new ClashResult { ElementAId = 3000000000, ElementBId = 4000000000,
    HostDocumentKey = "fixture", CategoryNameA = "Duct", CategoryNameB = "Beam",
    DisciplineA = Discipline.HVAC, DisciplineB = Discipline.Structural,
    ClashPoint = new Autodesk.Revit.DB.XYZ(1.25,2.5,3.75), Severity = ClashSeverity.Hard, GapMM = -20,
    LevelName = "Level 1", GridRef = "A/1", LinkFileB = "Structure.rvt", LinkInstanceB = "instance-1" };
var exporter = new ClashResolveAI.Services.BcfExportService();
var bcf = exporter.ExportClashes(new List<ClashResult> { issue }, "Verification & <test>", output);
Check(File.Exists(bcf), "BCF serialization works using cached metadata with no live elements");
File.WriteAllText(Path.Combine(output,"bcf-path.txt"),bcf);
Console.WriteLine(bcf);

var imported = exporter.ImportBcf(bcf);
Check(imported.Count == 1 && imported[0].Comments.Count == 1 && imported[0].Comments[0].Comment.Length > 0, "BCF comments survive export and import");

var copy=issue.ExportCopy();
Check(copy.ElementA==null && copy.ElementB==null && copy.ElementAId==issue.ElementAId, "Export snapshot carries IDs without live Revit element handles");
int advanced=0;
IEnumerable<int> Work(){for(int i=0;i<100;i++){advanced++;yield return i;}}
var job=new ClashResolveAI.ClashEngine.ScanJob(Work(),new List<ClashResult>(),new ClashResolveAI.ClashEngine.ScanStatistics());
job.Dispose();
Check(job.Cancelled && job.Advance(25) && advanced==0,"Cancelled scan does not execute pending work");
var unknown=issue.ExportCopy();unknown.ClashId="UNKNOWN";unknown.TestType=ClashTestType.Unverified;
var grouped=new ClashResolveAI.Engine.ClashGroupingEngine().GroupClashes(new List<ClashResult>{issue,unknown});
Check(grouped.All(g=>g.Clashes.Select(c=>c.TestType).Distinct().Count()==1),"Unverified issues cannot merge into confirmed clash groups");
// Differential broad-phase checks: compare the production octree to brute force.
var random=new Random(1977);
var boxes=new List<Aabb> {
    new Aabb(-1000,-1000,-1000,1000,1000,1000), // model-sized floor/duct
    new Aabb(0,0,0,0,0,0), // split-plane point
    new Aabb(-50,-1,-1,50,1,1), // crosses multiple octants
    new Aabb(-10,-10,-10,0,0,0)
};
for(int i=0;i<10000;i++) {
    double x=random.NextDouble()*1800-900,y=random.NextDouble()*1800-900,z=random.NextDouble()*1800-900;
    boxes.Add(new Aabb(x,y,z,x+random.NextDouble()*90,y+random.NextDouble()*90,z+random.NextDouble()*90));
}
BIMEngine.Core.Spatial.AABB Bounds(Aabb b)=>new(b.MinX,b.MinY,b.MinZ,b.MaxX,b.MaxY,b.MaxZ);
var tree=new BIMEngine.Core.Spatial.Octree(new(-1000,-1000,-1000,1000,1000,1000));
for(int i=0;i<boxes.Count;i++)tree.Insert(new(i,Bounds(boxes[i])));
var queries=new List<Aabb>{new Aabb(0,0,0,0,0,0),new Aabb(1000,1000,1000,1001,1001,1001),new Aabb(-2000,-2000,-2000,2000,2000,2000)};
for(int i=0;i<300;i++) {
    double x=random.NextDouble()*2400-1200,y=random.NextDouble()*2400-1200,z=random.NextDouble()*2400-1200;
    queries.Add(new Aabb(x,y,z,x+150,y+150,z+150));
}
bool equivalent=true,unique=true;
foreach(var q in queries) {
    var actual=tree.Query(Bounds(q)).Select(e=>(int)e.ElementId).ToList();
    var expected=Enumerable.Range(0,boxes.Count).Where(i=>AabbIntersector.Intersects(boxes[i],q)).ToHashSet();
    equivalent &= expected.SetEquals(actual);
    unique &= actual.Count==actual.Distinct().Count();
}
Check(equivalent,"Octree matches brute force for 303 queries over 10,004 boxes including boundaries and large spanning boxes");
Check(unique,"Octree emits each candidate once");
var emptyTree=new BIMEngine.Core.Spatial.Octree(new(0,0,0,0,0,0));
Check(!emptyTree.Query(Bounds(a)).Any(),"Empty octree returns no candidates");
for(int i=0;i<100;i++)emptyTree.Insert(new(i,new(0,0,0,0,0,0)));
Check(emptyTree.Query(Bounds(a)).Count()==100,"Degenerate identical boxes terminate at bounded depth");
Check(ModelChangePolicy.IsViewOnly(new[]{"ClashResolve — Navigate 3D","ClashResolveAI — Preview Snap 2D"}),"Navigation-only changes do not recursively start scans");
Check(!ModelChangePolicy.IsViewOnly(new[]{"ClashResolve — Navigate 3D","Move"}),"Mixed navigation/model transaction groups still trigger live detection");
Check(!ModelChangePolicy.IsViewOnly(new[]{"ClashResolve — Move pipe"}),"Product-prefixed model edits are not suppressed");
Check(!ModelChangePolicy.IsViewOnly(Array.Empty<string>()),"Unnamed model changes still invalidate scans");
var bimEntries=boxes.Select((b,i)=>new BIMEngine.Core.Spatial.SpatialEntry(i,new BIMEngine.Core.Spatial.AABB(b.MinX,b.MinY,b.MinZ,b.MaxX,b.MaxY,b.MaxZ))).ToArray();
var bimTree=BIMEngine.Core.Spatial.Octree.Build(bimEntries);
Check(bimTree.CountEntries()==boxes.Count,"Imported BIM Engine octree retains every snapshot");
Check(queries.All(q=>bimTree.Query(new BIMEngine.Core.Spatial.AABB(q.MinX,q.MinY,q.MinZ,q.MaxX,q.MaxY,q.MaxZ)).Select(e=>e.ElementId).OrderBy(i=>i).SequenceEqual(bimEntries.Where(e=>e.Bounds.Intersects(new BIMEngine.Core.Spatial.AABB(q.MinX,q.MinY,q.MinZ,q.MaxX,q.MaxY,q.MaxZ))).Select(e=>e.ElementId).OrderBy(i=>i))),"Imported loose octree matches brute force including spanning and boundary cases");
int cleanup=0;var cleanupJob=new ClashResolveAI.ClashEngine.ScanJob(Work(),new List<ClashResult>(),new ClashResolveAI.ClashEngine.ScanStatistics(),()=>cleanup++);cleanupJob.Dispose();cleanupJob.Dispose();Check(cleanup==1,"Cancelled job releases owned geometry exactly once");
var basisOk=true;
for(int i=0;i<500;i++) {
 var scene=new ClashResolveAI.Inspection.InspectionScene { AxisA=new ClashResolveAI.Inspection.Point3(random.NextDouble()-.5,random.NextDouble()-.5,random.NextDouble()-.5),AxisB=new ClashResolveAI.Inspection.Point3(0,0,1) };
 foreach(var mode in new[]{"2d","3d","section"})foreach(bool axisB in new[]{false,true})for(int corner=0;corner<4;corner++) {
 scene.Axes(mode,corner,axisB,out var right,out var up,out var forward);
 basisOk &= Math.Abs(right.Length-1)<1e-8&&Math.Abs(up.Length-1)<1e-8&&Math.Abs(forward.Length-1)<1e-8&&Math.Abs(right.Dot(up))+Math.Abs(right.Dot(forward))+Math.Abs(up.Dot(forward))<1e-8;
 }
}
Check(basisOk,"Inspection axes remain orthonormal in 12,000 camera orientations including vertical elements");
double measured=0;int records=0;bool disposed=false;
IEnumerable<int> TimedWork(){try{yield return 1;yield return 2;}finally{disposed=true;}}
using(var timed=ClashResolveAI.ClashEngine.ScanStatistics.Timed(TimedWork(),ms=>{measured+=ms;records++;}).GetEnumerator()) {
 Check(timed.MoveNext()&&timed.Current==1,"Timed iterator preserves values");
 double beforePause=measured;System.Threading.Thread.Sleep(30);
 Check(measured==beforePause&&records==1,"Phase timers exclude suspended slice time");
}
Check(disposed,"Cancelling timed iterator disposes underlying work");
IEnumerable<int> FailingWork(){yield return 0;throw new InvalidOperationException("fixture");}
records=0;bool threw=false;
try{foreach(var step in ClashResolveAI.ClashEngine.ScanStatistics.Timed(FailingWork(),ms=>records++)){} }catch(InvalidOperationException){threw=true;}
Check(threw&&records==2,"Phase timers record failed work and preserve exceptions");
var timings=new ClashResolveAI.ClashEngine.ScanStatistics { BooleanTests=2,SurfaceDistanceCalls=3 };
Check(timings.Summary.Contains("collect/index")&&timings.Summary.Contains("surface distance")&&timings.Summary.Contains("Boolean"),"Scan summary includes all phase timings");
Check(Enum.GetValues<UnverifiedReason>().All(reason=>new ClashResult {UnverifiedReason=reason}.ExportCopy().UnverifiedReason==reason),"Export snapshots preserve every unverified reason");
var profileStats=new ClashResolveAI.ClashEngine.ScanStatistics {Mode=ScanMode.HardOnly,BelowTolerance=2,SkippedClearance=7};
Check(profileStats.Summary.Contains("HardOnly")&&profileStats.Summary.Contains("below tolerance 2")&&profileStats.Summary.Contains("skipped clearance 7"),"Scan summary exposes the effective mode and skip counters");
Phase2Checks.Run(Check);
Phase4Checks.Run(Check);
Phase5Checks.Run(Check);
LiveGatewayChecks.Run(Check);
LiveReliabilityChecks.Run(Check);
LiveDiagnosticsChecks.Run(Check);
Console.WriteLine($"{passed} regression checks passed.");

