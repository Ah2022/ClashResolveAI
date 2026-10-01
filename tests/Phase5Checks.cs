using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;

internal static class Phase5Checks
{
    internal static void Run(Action<bool,string> check)
    {
        var radar=RadarDataStore.Instance;radar.Activate("p5-a");radar.Clear();radar.BeginSession();
        ClashResult Row(ResultOrigin origin,string doc="p5-a")=>new() {ClashId="pair",HostDocumentKey=doc,ElementAId=1,ElementBId=2,Origin=origin,LiveSessionId=radar.SessionId,TestType=ClashTestType.HardClash};
        var full=Row(ResultOrigin.Full);var live=Row(ResultOrigin.Live);
        radar.AddClashes(new[]{full,Row(ResultOrigin.Live,"foreign")});
        check(radar.ActiveCount==0,"Radar rejects Full results and foreign document results");
        radar.AddClashes(new[]{live});
        check(radar.GetVisible().Single()==live,"Current live session result appears in Radar");
        radar.IgnoreClash(live);
        check(full.Status!=ClashStatus.Ignored&&radar.ActiveCount==0,"Ignoring Live leaves identical Full pair unchanged");
        radar.AddClashes(new[]{Row(ResultOrigin.Live)});
        check(radar.ActiveCount==0,"Ignored live pair does not resurrect on re-detection");
        radar.Clear();radar.AddClashes(new[]{Row(ResultOrigin.Live)});radar.BeginSession();
        check(radar.GetVisible().Count==0&&radar.ActiveCount==1,"This session hides previous live session history");
        radar.ThisSession=false;
        check(radar.GetVisible().Count==1,"Disabling This session exposes only earlier Live history");
        radar.Activate("p5-b");check(radar.ActiveCount==0,"Radar document switch does not leak results");
        radar.Activate("p5-a");check(radar.ActiveCount==1&&!radar.ThisSession,"Radar restores document history and independent filters");
        radar.Close("p5-a");check(radar.ActiveCount==0,"Closing a document clears its live history");
        var revision=new ElementRevisionStore();revision.Changed("p5-a",new long[]{1},true);revision.Checked("p5-a",new long[]{1});
        check(revision.InputsStale("p5-a"),"Local completion cannot clear inputs-stale banner");
        revision.CheckedScope("p5-a",_=>true,false);check(revision.InputsStale("p5-a"),"Partial full scope cannot clear inputs-stale banner");
        revision.CheckedScope("p5-a",_=>true,true);check(!revision.InputsStale("p5-a"),"Complete Full Scan clears inputs-stale banner");
        check(live.ExportCopy().Origin==ResultOrigin.Live&&live.ExportCopy().LiveSessionId==live.LiveSessionId,"Export copy retains origin and live session");
        revision.Changed("p5-a",new long[]{42},false);revision.Checked("p5-a",new long[]{42});
        check(!revision.IsDirty("p5-a")&&revision.FullResultsStale("p5-a"),"Completed live check cannot certify an unchanged Full Scan snapshot after adding an element");
        revision.CheckedScope("p5-a",id=>id==1,false);
        check(revision.FullResultsStale("p5-a"),"Other-scope Full Scan cannot clear an uncovered added element");
        revision.CheckedScope("p5-a",id=>id==42,true);
        check(!revision.FullResultsStale("p5-a"),"Covering Full Scan certifies the Dashboard snapshot");
        revision.Changed("p5-a",new long[]{77,78},false);
        revision.CheckedScope("p5-a",_=>false,false);
        check(revision.FullResultsStale("p5-a"),"Scoped scan retains target and restored non-source dependants");
        revision.CheckedScope("p5-a",_=>false,true);
        check(!revision.FullResultsStale("p5-a")&&!revision.IsDirty("p5-a"),"Complete Full Scan supersedes target and Redo-restored dependant edits even when they are not sources");
    }
}
