using ClashResolveAI.LiveMonitor;

internal static class LiveGatewayChecks
{
    public static void Run(Action<bool,string> check)
    {
        var sessions=new LiveSessionRegistry();
        sessions.Activate("A");long a=sessions.Generation("A");
        check(sessions.IsCurrent("A",a),"Gateway session accepts its active document and generation");
        check(!sessions.IsCurrent("B",a)&&!sessions.IsCurrent("A",0),"Unknown documents and absent generations are rejected");
        sessions.Activate("B");
        check(!sessions.IsCurrent("A",a),"Switching projects rejects inactive requests");
        sessions.Activate("A");
        check(!sessions.IsCurrent("A",a),"Returning to a project cannot revive its old requests");
        a=sessions.Generation("A");sessions.Renew("A");
        check(!sessions.IsCurrent("A",a),"Clear or restart invalidates the previous session generation");
        a=sessions.Generation("A");sessions.Close("A");sessions.Activate("A");
        check(!sessions.IsCurrent("A",a),"Close and reopen cannot revive the old session");
        a=sessions.Generation("A");sessions.Clear();sessions.Activate("A");
        check(!sessions.IsCurrent("A",a),"Stopping the registry preserves monotonic generations across restart");
        a=sessions.Generation("A");sessions.Activate("A");
        check(sessions.Generation("A")==a,"Activating another view in the same project preserves the session");
        sessions.Activate("");
        check(!sessions.IsCurrent("A",a),"Family/no-project activation suspends live requests");

        LiveRequest Request(LiveOperation op,string key="",string doc="A",long generation=1) => new LiveRequest(doc,generation,op,key,4);
        var queue=new LiveRequestQueue();
        var first=Request(LiveOperation.Navigate3D,"first");var latest=Request(LiveOperation.Navigate2D,"latest");
        queue.Enqueue(first);queue.Enqueue(latest);
        check(queue.Count==1 && queue.Dequeue()==latest,"Navigation coalesces both view modes to the latest intention");
        queue.Enqueue(Request(LiveOperation.Inspect,"first"));queue.Enqueue(Request(LiveOperation.Inspect,"latest"));
        check(queue.Count==1 && queue.Dequeue()!.ClashKey=="latest","Inspection coalesces to the latest selected clash");
        queue.Enqueue(Request(LiveOperation.Refresh));queue.Enqueue(Request(LiveOperation.Refresh));
        check(queue.Count==1 && queue.Coalesced==3,"Repeated refresh requests are coalesced and counted");
        queue.Clear();queue.Enqueue(Request(LiveOperation.Refresh));queue.Enqueue(Request(LiveOperation.Cancel));queue.Enqueue(Request(LiveOperation.Refresh));
        check(queue.Count==3 && queue.Dequeue()!.Operation==LiveOperation.Refresh && queue.Dequeue()!.Operation==LiveOperation.Cancel && queue.Dequeue()!.Operation==LiveOperation.Refresh,"Cancellation remains an ordered barrier between checks");
        queue.Enqueue(Request(LiveOperation.Clear));queue.Enqueue(Request(LiveOperation.Clear));queue.Enqueue(Request(LiveOperation.Pin));queue.Enqueue(Request(LiveOperation.Pin));
        check(queue.Count==4,"Explicit clears and pin actions are never silently coalesced");
        queue.Clear();queue.Enqueue(Request(LiveOperation.Advance));queue.Enqueue(Request(LiveOperation.Advance));
        check(queue.Count==1,"Scan wake requests coalesce instead of flooding the queue");
        queue.Enqueue(Request(LiveOperation.Advance,doc:"B"));queue.Enqueue(Request(LiveOperation.Advance,generation:2));
        check(queue.Count==3,"Coalescing never merges distinct documents or generations");
        queue.RemoveDocument("A");
        check(queue.Count==1 && queue.Dequeue()!.DocumentKey=="B","Document closure removes only that document's queued operations");
        queue=new LiveRequestQueue(2);queue.Enqueue(Request(LiveOperation.Clear));queue.Enqueue(Request(LiveOperation.Pin));
        check(!queue.Enqueue(Request(LiveOperation.Cancel)) && queue.Count==2,"Queue overflow reports rejection and retains existing ordered operations");
        queue.Clear();
        check(queue.Dequeue()==null && first.RequestId!=latest.RequestId && first.DocumentKey=="A" && first.SessionGeneration==1 && first.GeometryRevision==4,"Requests have independent correlation IDs and frozen identity/revision values");
    }
}
