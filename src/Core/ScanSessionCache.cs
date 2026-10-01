using Autodesk.Revit.DB;
using ClashResolveAI.Links;
using ClashResolveAI.Rules;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Core
{
    // Revit API thread only. No geometry is retained between jobs.
    internal static class ScanSessionCache
    {
        private sealed class State
        {
            public LinkedModelManager Links = null!;
            public readonly Dictionary<int,ElementMulticategoryFilter> Filters=new Dictionary<int,ElementMulticategoryFilter>();
            public RulesEngine? Rules;
            public string RuleKey="";
        }
        private static readonly Dictionary<string,State> States=new Dictionary<string,State>();
        private static State Get(Document doc)
        {
            string key=DocumentSession.Key(doc);
            if(!States.TryGetValue(key,out var state))States[key]=state=new State { Links=new LinkedModelManager(doc) };
            return state;
        }
        public static LinkedModelManager Links(Document doc)=>Get(doc).Links;
        public static ElementMulticategoryFilter Filter(Document doc,AppSettings settings)
        {
            var state=Get(doc);int key=(settings.IncludeGenericModels?1:0)|(settings.IncludeInsulation?2:0);
            if(!state.Filters.TryGetValue(key,out var filter))state.Filters[key]=filter=ClashEngine.ClashEngine.BuildCategoryFilter(settings);
            return filter;
        }
        public static RulesEngine Rules(Document doc,string name)
        {
            var state=Get(doc);string key=RulesEngine.CacheKey(name);
            if(state.Rules==null||state.RuleKey!=key){state.Rules=new RulesEngine(name);state.RuleKey=key;}
            return state.Rules;
        }
        public static void Changed(Document doc,IEnumerable<ElementId> ids)
        {
            if(!States.TryGetValue(DocumentSession.Key(doc),out var state))return;
            if(ids.Any(id=>state.Links.IsKnownLink(id)||doc.GetElement(id) is RevitLinkInstance||doc.GetElement(id) is RevitLinkType))
                state.Links.InvalidateCache();
        }
        public static void Close(Document doc)
        {
            string key=DocumentSession.Key(doc);
            if(!States.TryGetValue(key,out var state))return;
            foreach(var filter in state.Filters.Values)filter.Dispose();
            States.Remove(key);
        }
        public static void Clear()
        {
            foreach(var state in States.Values)foreach(var filter in state.Filters.Values)filter.Dispose();
            States.Clear();
        }
    }
}
