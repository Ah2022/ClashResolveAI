using ClashResolveAI.Core;

internal static class Phase4Checks
{
    internal static void Run(Action<bool,string> check)
    {
        var ledger=new LiveLedgerStore();
        ledger.Note("A",1,"uid1",LedgerOrigin.Drawn);ledger.Note("B",1,"uid-b",LedgerOrigin.Edited);
        ledger.Checked("A",new HashSet<long>{1});
        check(!ledger.HasUnchecked("A")&&ledger.HasUnchecked("B"),"Ledger checked state is document-scoped despite matching element IDs");
        ledger.Note("A",1,"uid1",LedgerOrigin.Edited);
        check(ledger.HasUnchecked("A")&&ledger.Entries("A").Single().Origin==LedgerOrigin.Drawn,"Editing a drawn element preserves its origin and requires another check");
        ledger.LiveIds("A",_=>null);
        check(ledger.Entries("A").Count==0,"Deletion and undo prune missing ledger elements");
        ledger.Note("A",1,"uid1",LedgerOrigin.Drawn);ledger.LiveIds("A",_=>"different-unique-id");
        check(ledger.Entries("A").Count==0,"Reused numeric IDs cannot alias an old ledger entry");
        ledger.Note("A",1,"uid1",LedgerOrigin.Drawn);ledger.Checked("A",new HashSet<long>{1});ledger.Pending("A",new HashSet<long>{1});
        check(ledger.HasUnchecked("A"),"An explicit ledger re-check marks previously checked entries pending");
        ledger.Clear("A");check(ledger.Entries("B").Count==1,"Clearing one live session preserves other documents");
        ledger.Clear();check(ledger.Entries("B").Count==0,"Stopping the monitor can clear every session");
        var revisions=new ElementRevisionStore();
        var a=new ClashResult {HostDocumentKey="A",ElementAId=1,ElementBId=2};
        var b=new ClashResult {HostDocumentKey="A",ElementAId=3,ElementBId=4};
        revisions.Changed("A",new long[]{1},false);
        check(revisions.IsStale(a)&&!revisions.IsStale(b),"Editing one element leaves unrelated results current and pinnable");
        var foreign=new ClashResult {HostDocumentKey="B",ElementAId=1,ElementBId=2};
        check(!revisions.IsStale(foreign),"Element revisions cannot leak between documents");
        var linked=new ClashResult {HostDocumentKey="A",ElementAId=1,ElementBId=9,LinkInstanceA="link"};
        check(!revisions.IsStale(linked),"A host edit does not invalidate an equal numeric ID inside a link");
        revisions.Checked("A",new long[]{1});a.GeometryRevision=revisions.Revision;
        check(!revisions.IsDirty("A")&&!revisions.IsStale(a),"Local completion clears pending report state and stamps current geometry");
        revisions.Changed("A",Array.Empty<long>(),true);
        check(revisions.IsDirty("A")&&revisions.IsStale(a)&&revisions.IsStale(linked),"Model input changes invalidate dependent inspection and report state");
        revisions.CheckedScope("A",_=>true,false);
        check(revisions.IsDirty("A"),"Scoped verification cannot clear the model-input dirty flag");
        revisions.InputsCurrent("A");check(!revisions.IsDirty("A"),"Completed full input verification clears the report gate");
        revisions.Changed("A",new long[]{1,2},false);revisions.CheckedScope("A",id=>id==1,false);
        check(revisions.IsDirty("A"),"Scoped completion keeps unchecked elements pending");
        revisions.Close("A");check(!revisions.IsDirty("A"),"Document close clears revision state");
    }
}
