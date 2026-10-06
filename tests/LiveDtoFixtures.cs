using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;
internal static class LiveDtoFixtures
{
    public static LiveClashDto From(ClashResult row,string? uidA=null,string? uidB=null)=>new(row,
        new LiveElementIdentity(row.ElementAId,uidA??"uid:"+row.ElementAId,row.HostDocumentKey,row.LinkInstanceA,"v1"),
        new LiveElementIdentity(row.ElementBId,uidB??"uid:"+row.ElementBId,row.HostDocumentKey,row.LinkInstanceB,"v1"),
        new LivePoint(0,0,0),"fixture-environment");
}
