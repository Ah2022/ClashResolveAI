using BIMEngine.Core.Spatial;
using ClashResolveAI.Core;
using Newtonsoft.Json;

internal static class Phase78Checks
{
    public static void Run(Action<bool,string> check)
    {
        var basis=new ScanPlan("project",ScanProfile.Advanced,ScanStage.Coordination,ScanMode.HardAndClearance,true,true,false,false,new[]{"link"},levelIds:new[]{"floor"},
            ruleSetContent:"{\"rules\":{\"Generic\":{\"clearance_mm\":100}}}");
        var selection=new ScanProfileSelection {Profile=ScanProfile.DetailedCoordination,Links=new(){"link"},LevelIds=new(){"floor"},ClearanceRules=new(){"Generic"},CoordinationRadiusMM=304.8};
        var detailed=ScanProfiles.Create(basis,selection);var hard=ScanStaging.HardPass(detailed);
        check(ScanStaging.IsStaged(detailed)&&!ScanStaging.IsStaged(basis),"Only the Detailed profile schedules the hard-first workflow");
        check(hard.Mode==ScanMode.HardOnly&&hard.Stage==ScanStage.HardClash&&hard.ClearanceRuleKeys.Count==0&&!hard.LinkToLink,"Stage one is a hard-only job with no implicit cross-link work");
        check(hard.LevelIds.SequenceEqual(detailed.LevelIds)&&hard.Relationships().Count==detailed.Relationships().Count,"Stage one preserves reviewed floor and relationship scope");
        var confirmed=new ClashResult {ClashId="confirmed",TestType=ClashTestType.HardClash,OverlapVolumeMM3=2,ClashPoint=new(0,0,0)};
        var possible=new ClashResult {ClashId="possible",TestType=ClashTestType.Unverified,OverlapVolumeMM3=100};
        var nearMiss=new ClashResult {ClashId="clearance",TestType=ClashTestType.ClearanceClash,OverlapVolumeMM3=0};
        check(ScanStaging.ClearancePass(detailed,new[]{possible,nearMiss})==null,"Unverified and clearance findings never seed confirmed clearance areas");
        check(ScanStaging.ClearancePass(detailed,Array.Empty<ClashResult>())==null,"No confirmed overlaps skips stage two instead of scanning the whole model");
        var clear=ScanStaging.ClearancePass(detailed,new[]{confirmed,possible,nearMiss})!;
        check(clear.Stage==ScanStage.Clearance&&clear.CoordinationAreas.Count==1&&clear.CoordinationAreas[0].EvidenceId=="confirmed","Stage two freezes only confirmed hard-clash area evidence");
        check(clear.CoordinationAreas[0].RadiusFeet==1&&clear.ClearanceRuleKeys.SequenceEqual(detailed.ClearanceRuleKeys),"Clearance stage preserves reviewed radius and selected rules");
        check(!clear.WholeModelScope&&!clear.LinkToLink,"Local clearance cannot claim entire-model coverage or schedule cross links");
        check(ScanStaging.AllowsPair(clear,new AABB(0,0,0,.2,.2,.2),new AABB(.1,.1,0,.3,.3,.2)),"Elements touching the same confirmed area can be tested");
        check(!ScanStaging.AllowsPair(clear,new AABB(0,0,0,.2,.2,.2),new AABB(20,0,0,21,1,1)),"An endpoint outside confirmed areas is pruned before geometry");
        var round=JsonConvert.DeserializeObject<ScanPlan>(JsonConvert.SerializeObject(clear))!;
        check(round.CoordinationAreas.Count==1&&ScanProfiles.Fingerprint(round)==ScanProfiles.Fingerprint(clear),"Confirmed areas and radius survive immutable plan persistence");
        var scope=new ScanSpatialScope(clear,new[]{new ScanFloor("floor","L01",0)});
        check(scope.Includes(new AABB(0,0,0,1,1,1),"","",""),"Confirmed-area source inside the reviewed floor remains eligible");
        check(!scope.Includes(new AABB(20,0,0,21,1,1),"","",""),"A source outside confirmed areas is excluded even on the selected floor");
        var coverage=new ScanScope("project");coverage.Note(1,"","");coverage.RestrictPairs((a,la,b,lb)=>a==1&&b==2,new {ConfirmedArea="fixture"});
        check(coverage.ContainsPair("project",1,"",2,"","inside",true),"Area-aware coverage records reliable clearance absence inside its tested pair scope");
        check(!coverage.ContainsPair("project",1,"",3,"","outside",true),"Untested clearance outside an area cannot become resolved");
        check(!coverage.IsHostReliable(1),"Area-limited evaluation cannot certify a whole host element for Live freshness");
        var old=new ClashResult {HostDocumentKey="project",ElementAId=1,ElementBId=3,TestType=ClashTestType.ClearanceClash,Status=ClashStatus.Active};
        ResultLifecycle.Merge(new[]{old},new(),ScanMode.HardAndClearance,coverage.Contains);
        check(old.Status==ClashStatus.Active,"Partial clearance reconciliation retains existing untested findings");
        bool invalid=false;try{new ScanCoordinationArea("bad",double.NaN,0,0,1);}catch(ArgumentException){invalid=true;}
        check(invalid,"Invalid area coordinates cannot enter a scan plan");
        selection.CoordinationRadiusMM=0;invalid=false;try{ScanProfiles.Create(basis,selection);}catch(ArgumentOutOfRangeException){invalid=true;}
        check(invalid,"An unbounded zero-area radius is rejected explicitly");
        var spread=new Octree(new AABB(-1,-1,-1,2000,50,50));
        for(int i=0;i<256;i++)spread.Insert(new SpatialEntry(i,new AABB(i*5,0,0,i*5+1,1,1)));
        var region=new AABB(-1,-1,-1,1300,2,2);
        check(spread.Query(region).Select(e=>e.ElementId).OrderBy(id=>id).SequenceEqual(spread.Enumerate(region).Select(e=>e.ElementId).OrderBy(id=>id)),"Streaming queries traverse subdivided nodes with no parent entries");
    }
}
