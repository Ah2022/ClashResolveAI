using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.LiveMonitor
{
    internal static class LiveSessionLedger
    {
        internal static readonly LiveLedgerStore Store=new LiveLedgerStore();
        public static void Note(Document doc,IEnumerable<ElementId> ids,LedgerOrigin origin)
        {
            string key=DocumentSession.Key(doc);
            foreach(var id in ids){var element=doc.GetElement(id);if(element!=null&&element.IsValidObject&&!(element is ElementType))Store.Note(key,id.Value,element.UniqueId,origin);}
        }
        public static List<ElementId> LiveIds(Document doc)=>Store.LiveIds(DocumentSession.Key(doc),id=>doc.GetElement(new ElementId(id))?.UniqueId).Select(id=>new ElementId(id)).ToList();
        public static int Count=>Store.Entries(DocumentSession.CurrentKey).Count;
        public static void Clear(Document doc)=>Store.Clear(DocumentSession.Key(doc));
    }
}
