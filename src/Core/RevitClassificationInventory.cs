using Autodesk.Revit.DB;
using ClashResolveAI.Links;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Core
{
    internal static class RevitClassificationInventory
    {
        // Enumerate only in the Revit API context. Returned rows contain copied metadata.
        public static IEnumerable<ClassificationReviewSample> Collect(Document host,ScanPlan plan)
        {
            var relationships=plan.Relationships();
            var needed=new HashSet<string>(relationships.SelectMany(p=>new[]{p.Source,p.Target}));
            var documents=new Dictionary<string,Tuple<Document,HashSet<string>>>();
            void Add(Document doc,string label) {
                string key=DocumentSession.Key(doc);
                if(!documents.TryGetValue(key,out var item))documents[key]=item=Tuple.Create(doc,new HashSet<string>());
                item.Item2.Add(label);
            }
            if(needed.Contains(""))Add(host,"Host");
            foreach(var link in new LinkedModelManager(host).GetLoadedLinks().Where(l=>needed.Contains(l.Instance.UniqueId)))
                Add(link.LinkDoc,link.FileName+" [instance "+link.Instance.Id.Value+"]");
            using var filter=ClashEngine.ClashEngine.BuildCategoryFilter(new AppSettings {IncludeGenericModels=true,IncludeInsulation=true});
            foreach(var document in documents.Values) {
                // Views, annotations, rooms, spaces and other unsupported categories are never enumerated.
                using var collector=new FilteredElementCollector(document.Item1);
                foreach(var id in collector.WherePasses(filter).WhereElementIsNotElementType().ToElementIds()) {
                    var element=document.Item1.GetElement(id);if(element==null||!element.IsValidObject)continue;
                    ClassificationInput input;
                    try {input=RevitElementClassifier.ReadInput(element);}
                    catch(Exception ex) {
                        Diagnostics.Log("Classification review could not read element "+id.Value,ex);
                        input=new ClassificationInput {CategoryId=element.Category?.Id.Value??0,Category=element.Category?.Name??"",FamilyType=ElementCollector.GetFamilyTypeName(element)};
                    }
                    yield return new ClassificationReviewSample(input,string.Join("; ",document.Item2.OrderBy(s=>s,StringComparer.Ordinal)));
                }
            }
        }
    }
}
