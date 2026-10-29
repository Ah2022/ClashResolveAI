using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;

internal static class LiveReliabilityChecks
{
    public static void Run(Action<bool,string> check)
    {
        var clock=new DateTime(2026,10,5,0,0,0,DateTimeKind.Utc);
        var accumulator=new ChangeAccumulator();
        accumulator.Record("A",1,"u1",LiveChangeKind.Added,clock);
        check(!accumulator.Ready("A",clock.AddMilliseconds(299),300)&&accumulator.Ready("A",clock.AddMilliseconds(300),300),"Local debounce waits for the configured quiet period");
        accumulator.Record("A",1,"u1",LiveChangeKind.Modified,clock.AddMilliseconds(200));
        check(accumulator.Count("A")==1&&!accumulator.Ready("A",clock.AddMilliseconds(450),300),"Burst changes merge and reset the quiet-period clock");
        accumulator.Record("A",1,"u1",LiveChangeKind.Modified,clock.AddMilliseconds(1950));
        long committedSequence=accumulator.Watermark("A");
        accumulator.Record("A",1,"u1",LiveChangeKind.Check,clock.AddMilliseconds(1960));
        check(accumulator.Pending("A").Single().Kind==LiveChangeKind.Modified&&accumulator.Watermark("A")==committedSequence,"Selection and recheck requests preserve committed change origin and watermark");
        check(accumulator.Ready("A",clock.AddMilliseconds(2000),500),"Continuous editing cannot starve a batch beyond maximum age");
        var first=accumulator.Detach("A",7,clock.AddSeconds(2));
        check(first.DocumentKey=="A"&&first.SessionGeneration==7&&first.Changes.Count==1&&accumulator.Count("A")==0,"Batch detachment transfers plain changes with frozen document and generation");
        accumulator.Record("A",1,"u1",LiveChangeKind.Deleted,clock.AddSeconds(3));
        accumulator.Restore(first);
        check(accumulator.Pending("A").Single().Kind==LiveChangeKind.Deleted&&accumulator.Pending("A").Single().Sequence>first.Watermark,"Restoring an interrupted batch never overwrites a later edit of the same ID");
        accumulator.AcknowledgeThrough("A",first.Watermark,_=>true);
        check(accumulator.Count("A")==1,"A Full Scan watermark cannot acknowledge a post-start deletion");
        accumulator.Record("A",2,"u2",LiveChangeKind.Modified,clock.AddSeconds(3));
        accumulator.Record("B",1,"foreign",LiveChangeKind.Modified,clock);
        long watermark=accumulator.Watermark("A");accumulator.AcknowledgeThrough("A",watermark,id=>id==2);
        check(accumulator.Count("A")==1&&accumulator.Count("B")==1,"Scoped acknowledgement preserves uncovered elements and other documents");
        var deletion=accumulator.Detach("A",7,clock.AddSeconds(4));
        accumulator.Record("A",1,"replacement",LiveChangeKind.Added,clock.AddSeconds(5));accumulator.Restore(deletion);
        check(accumulator.Pending("A").Single().UniqueId=="replacement","Numeric ID reuse preserves the latest endpoint identity");
        accumulator.Clear("A");long prior=accumulator.Watermark("A");accumulator.Record("A",3,"u3",LiveChangeKind.Check,clock);
        check(accumulator.Watermark("A")>prior,"Clear keeps change sequences monotonic");
        accumulator.Record("A",4,"u4",LiveChangeKind.Modified,clock);accumulator.Record("A",5,"u5",LiveChangeKind.Modified,clock);
        var bounded=accumulator.Detach("A",8,clock,2);
        check(bounded.Changes.Count==2&&accumulator.Count("A")==1,"Large change sets split into bounded batches without dropping the tail");
        accumulator.Restore(bounded);accumulator.Close("A");check(accumulator.Count("B")==1,"Closing one accumulator preserves other document queues");

        var state=new LiveSchedulingState();state.Cancelled=true;state.ProtectionPaused=true;
        check(!state.CanRun,"Live cancellation and responsiveness protection pause local work");
        state.ResumeRequested();check(state.CanRun,"Manual Radar recheck resumes local work without Full Scan");

        ClashResult Row(long a=1,long b=2)=>new(){HostDocumentKey="dto",ElementAId=a,ElementBId=b,Origin=ResultOrigin.Live,Status=ClashStatus.Active,GeometryRevision=5,SessionGeneration=7};
        var original=Row();var dto=LiveDtoFixtures.From(original);
        var swapped=Row(2,1);check(dto.ClashKey==LiveDtoFixtures.From(swapped).ClashKey,"Stable DTO clash identity is symmetric in endpoint order");
        var renumbered=Row(11,22);check(dto.ClashKey==LiveDtoFixtures.From(renumbered,"uid:1","uid:2").ClashKey,"UniqueIds preserve clash identity when numeric display IDs change");
        check(dto.ClashKey!=LiveDtoFixtures.From(original,"replacement","uid:2").ClashKey,"Reused numeric IDs cannot alias a different endpoint UniqueId");
        original.LinkInstanceB="instance-A";var linked=LiveDtoFixtures.From(original);original.LinkInstanceB="instance-B";
        check(linked.ClashKey!=LiveDtoFixtures.From(original).ClashKey,"Repeated placements of the same link keep distinct stable clash keys");
        original.CategoryNameA="mutated";original.Status=ClashStatus.Resolved;
        check(dto.CategoryNameA!="mutated"&&dto.Status==ClashStatus.Active,"DTO display and lifecycle values are detached from mutable engine results");
        var radar=RadarDataStore.Instance;radar.Activate("dto");radar.Clear();radar.ThisSession=false;radar.AddClashes(new[]{dto});radar.IgnoreClash(dto);
        check(dto.Status==ClashStatus.Active&&radar.GetAll().Single().Status==ClashStatus.Ignored,"Ignoring a DTO replaces the stored snapshot without mutating UI-owned rows");
        radar.Clear();radar.AddClashes(new[]{dto});radar.Reconcile(new HashSet<long>{1},Array.Empty<LiveClashDto>(),ScanMode.HardOnly,_=>false);
        check(radar.ActiveCount==1&&radar.GetActive().Single().Verification==LiveVerificationState.Unverified,"Incomplete coverage retains a missing clash as explicitly unverified");
        var resolved=radar.Reconcile(new HashSet<long>{1},Array.Empty<LiveClashDto>(),ScanMode.HardOnly,_=>true);
        check(radar.ActiveCount==0&&resolved.ResolvedClashes==1,"Verified source coverage resolves an old clash after movement");
        var recurring=radar.Reconcile(new HashSet<long>{1},new[]{dto},ScanMode.HardOnly,_=>true);
        check(radar.ActiveCount==1&&recurring.NewClashes==0&&radar.GetActive().Single().Status==ClashStatus.Active,"Undo/Redo reactivates a stable pair without duplicate new-clash alerts");
        radar.MarkStale("dto",new HashSet<long>{1},"edited");check(radar.GetActive().Single().Verification==LiveVerificationState.Stale&&dto.Verification==LiveVerificationState.Verified,"Revision invalidation replaces snapshots and marks stale rows explicitly");
        var scope=new ScanScope("dto");scope.Note(1,"","");scope.MissingSource(1,"");scope.MissingPair(dto.LegacyPairKey);
        check(!scope.IsHostReliable(1)&&!scope.ContainsPair("dto",1,"",2,"",dto.LegacyPairKey),"Boolean/missing-geometry failures block both source certification and pair resolution");
        var safeTypes=new[]{typeof(LiveClashDto),typeof(LiveElementIdentity),typeof(LivePoint)};
        check(safeTypes.SelectMany(t=>t.GetProperties()).All(p=>!p.PropertyType.FullName!.StartsWith("Autodesk.Revit",StringComparison.Ordinal))&&safeTypes.SelectMany(t=>t.GetProperties()).All(p=>p.SetMethod==null||!p.SetMethod.IsPublic),"DTO boundary has no Revit property types or publicly writable properties");
        check(!typeof(LiveClashDto).GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Any(f=>f.FieldType==typeof(ClashResult)),"DTOs retain no hidden engine result references");
        radar.Clear();
    }
}
