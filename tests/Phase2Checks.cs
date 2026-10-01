using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;

internal static class Phase2Checks
{
    private static ClashResult Row(long id,ClashTestType type=ClashTestType.HardClash,UnverifiedReason reason=UnverifiedReason.SurfaceClearance)=>new() {
        ClashId="row-"+id,HostDocumentKey="fixture",ElementAId=id,ElementBId=100000+id,TestType=type,UnverifiedReason=reason,Status=ClashStatus.Active
    };
    internal static void Run(Action<bool,string> check)
    {
        var clearance=Enumerable.Range(1,739).Select(i=>Row(i,ClashTestType.ClearanceClash)).ToList();
        var states=new[]{ClashStatus.New,ClashStatus.Active,ClashStatus.InReview,ClashStatus.Approved,ClashStatus.Ignored,ClashStatus.Closed,ClashStatus.Resolved};
        for(int i=0;i<clearance.Count;i++)clearance[i].Status=states[i%states.Length];
        var before=clearance.Select(c=>c.Status).ToArray();
        var hard=Row(800);var possible=Row(801,ClashTestType.Unverified,UnverifiedReason.SolidTest);
        var surface=Row(802,ClashTestType.Unverified);var missing=Row(803,ClashTestType.Unverified,UnverifiedReason.MissingGeometry);
        var old=clearance.Concat(new[]{hard,possible,surface,missing}).ToList();
        var merged=ResultLifecycle.Merge(old,new(),ScanMode.HardOnly,_=>true);
        check(clearance.Select(c=>c.Status).SequenceEqual(before)&&merged.RetainedClearance==739,"Hard-only merge preserves all 739 clearance rows and every status");
        check(hard.Status==ClashStatus.Resolved&&possible.Status==ClashStatus.Resolved&&merged.Touched.Count==2,"Only evaluated hard and possible-hard rows auto-resolve");
        check(surface.Status==ClashStatus.Active&&missing.Status==ClashStatus.Active,"Surface and missing-geometry rows are not auto-resolved by hard-only checks");
        var ignored=Row(804);ignored.Status=ClashStatus.Ignored;var closed=Row(805);closed.Status=ClashStatus.Closed;
        ResultLifecycle.Merge(new[]{ignored,closed},new(),ScanMode.HardOnly,_=>true);
        check(ignored.Status==ClashStatus.Ignored&&closed.Status==ClashStatus.Closed,"Ignored and closed hard rows retain lifecycle status");
        var combined=Row(806,ClashTestType.ClearanceClash);
        ResultLifecycle.Merge(new[]{combined},new(),ScanMode.HardAndClearance,_=>true);
        check(combined.Status==ClashStatus.Resolved,"Combined mode still resolves re-tested clearance rows");
        var lower=Row(900);lower.LevelName="L1";var upper=Row(901);upper.LevelName="L2";
        var scope=new ScanScope("fixture");scope.Note(lower.ElementAId,"","");
        var scoped=ResultLifecycle.Merge(new[]{lower,upper},new(),ScanMode.HardOnly,scope.Contains);
        check(scoped.Rows.Count==2&&lower.Status==ClashStatus.Resolved&&upper.Status==ClashStatus.Active,"Level-scoped source coverage preserves other-level rows");
        var linked=Row(900);linked.LinkInstanceA="link-A";
        check(!scope.Contains(linked),"Scope distinguishes equal host and linked numeric IDs");
        var linkScope=new ScanScope("fixture");linkScope.Note(900,"","link-A");
        var outsideLink=Row(900);outsideLink.LinkInstanceB="link-B";
        check(!linkScope.Contains(outsideLink),"Link-restricted scope retains rows against other links");
        var reversed=Row(900);reversed.LinkInstanceA="link-A";reversed.ElementAId=100900;reversed.ElementBId=900;
        check(linkScope.Contains(reversed),"Evaluated-source coverage is symmetric in pair orientation");
        var foreign=Row(900);foreign.HostDocumentKey="other";
        check(!scope.Contains(foreign)&&!new ScanScope("fixture").Contains(upper),"Empty coverage and other-document rows are not evaluated");
        var missingScope=new ScanScope("fixture");missingScope.Note(900,"","");missingScope.MissingPair(lower.NormalizedKey);
        check(!missingScope.Contains(lower),"A pair with missing solids cannot be auto-resolved from absence of hard-only output");
        var recurrence=Row(800);hard.Metadata.Comments="keep assignment/history";var firstSeen=hard.DetectedAt;
        var recurring=ResultLifecycle.Merge(new[]{hard},new(){recurrence},ScanMode.HardOnly,_=>true);
        check(recurring.Rows.Count==1&&recurrence.Status==ClashStatus.Active&&recurrence.Metadata.Comments==hard.Metadata.Comments&&recurrence.DetectedAt==firstSeen,"Recurring hard pair reactivates and preserves lifecycle metadata");
        var promoted=Row(950,ClashTestType.ClearanceClash);promoted.Status=ClashStatus.InReview;
        var nowHard=Row(950);var promotion=ResultLifecycle.Merge(new[]{promoted},new(){nowHard},ScanMode.HardOnly,_=>true);
        check(promotion.Rows.Count==1&&promotion.Rows[0].TestType==ClashTestType.HardClash&&nowHard.Status==ClashStatus.InReview,"A newly confirmed hard pair replaces its old clearance classification without losing status");
        var filter=new ResultViewFilter();var critical=Row(960);critical.Severity=ClashSeverity.Critical;
        var viewRows=new[]{critical,possible,combined,surface,missing};
        check(viewRows.Where(filter.Matches).Count()==2,"Hard-only default view includes critical hard and possible hard, excluding other unverified types");
        filter.Types=ResultTypes.Unverified;
        check(viewRows.Where(filter.Matches).SequenceEqual(new[]{surface,missing}),"Unverified filter excludes possible-hard rows");
        filter.Types=ResultTypes.All;filter.Level="L2";
        check(new[]{lower,upper}.Where(filter.Matches).Single()==upper,"Shared view predicate combines result types with existing level filters");
        var radar=RadarDataStore.Instance;radar.Activate("fixture");radar.Clear();
        ClashResult Live(long id,ClashTestType type=ClashTestType.HardClash){var c=Row(id,type);c.Origin=ResultOrigin.Live;c.LiveSessionId=radar.SessionId;return c;}
        var radarClearance=Enumerable.Range(1,739).Select(i=>Live(i,ClashTestType.ClearanceClash)).ToList();
        radar.AddClashes(radarClearance.Concat(new[]{Live(999)}));
        radar.Reconcile(new HashSet<long>(Enumerable.Range(1,999).Select(i=>(long)i)),new(),ScanMode.HardOnly);
        check(radar.ActiveCount==739&&radar.GetActive().All(c=>c.TestType==ClashTestType.ClearanceClash&&c.Status==ClashStatus.Active),"Radar hard-only reconcile retains 739 clearance rows while removing resolved hard rows");
        radar.ReplaceSnapshot(new[]{Live(998)});
        check(radar.ActiveCount==1&&radar.GetActive()[0].ClashId=="row-998","Authoritative radar publication removes purged rows without treating them as scan results");
        radar.Clear();
    }
}
