using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using ClashResolveAI.ClashEngine;
using ClashResolveAI.Links;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClashResolveAI.Core
{
    internal static partial class IntegrationVerification
    {
        // Runs only in a separately launched verification process, with newly created documents.
        private static void VerifyScanPlanningNative(UIApplication app)
        {
            Check(app.ActiveUIDocument==null,"Scan planning verification starts without an open user project");
            const string rules="{\"name\":\"ScanPlanningFixture\",\"rules\":{\"Generic\":{\"clearance_mm\":0}}}";
            DirectShape TypedBox(Document doc,BuiltInCategory category,string name,XYZ origin) {
                var shape=Box(doc,category,origin);
                var type=DirectShapeType.Create(doc,name,new ElementId((long)category));shape.SetTypeId(type.Id);return shape;
            }
            void AddElements(Document doc) {
                TypedBox(doc,BuiltInCategory.OST_MechanicalEquipment,"AHU-01",XYZ.Zero);
                TypedBox(doc,BuiltInCategory.OST_StructuralColumns,"COLUMN-01",new XYZ(.5,.5,0));
                TypedBox(doc,BuiltInCategory.OST_MechanicalEquipment,"HANGER-M10",XYZ.Zero);
                TypedBox(doc,BuiltInCategory.OST_GenericModel,"MEDICAL OUTLET",XYZ.Zero);
            }
            ScanPlan Plan(Document doc,IEnumerable<string> ids,bool host=true,bool hostLinks=true,bool inside=false,bool cross=false,
                IEnumerable<ScanModelPair>? pairs=null,CoordinationCandidatePolicy? policy=null,IEnumerable<ClassificationMapping>? mappings=null,
                string level="",ScanStage stage=ScanStage.HardClash) =>
                new ScanPlan(DocumentSession.Key(doc),ScanProfile.Advanced,stage,
                    stage==ScanStage.InsulationClearance?ScanMode.HardAndClearance:ScanMode.HardOnly,
                    host,hostLinks,inside,cross,ids,pairs,levelIds:level==""?null:new[]{level},ruleSetName:"ScanPlanningFixture",ruleSetContent:rules,
                    candidatePolicy:policy ?? new CoordinationCandidatePolicy(includeGenericModels:true,includeInsulation:true),mappings:mappings);
            (List<ClashResult> Rows,ScanStatistics Stats) Run(Document doc,ScanPlan plan,bool native=false) {
                var engine=new ClashResolveAI.ClashEngine.ClashEngine(doc);
                using var job=engine.CreatePlannedJob(plan,native);while(!job.Advance(25)) { }
                return (job.Results,job.Statistics);
            }
            var evidence=new List<object>();
            var excluded=app.Application.NewProjectDocument(UnitSystem.Metric);
            try {
                using(var tx=new Transaction(excluded,"Excluded-only scan fixture")) {
                    tx.Start();TypedBox(excluded,BuiltInCategory.OST_MechanicalEquipment,"HANGER M10",XYZ.Zero);
                    TypedBox(excluded,BuiltInCategory.OST_GenericModel,"MEDICAL OUTLET",XYZ.Zero);tx.Commit();
                }
                var run=Run(excluded,Plan(excluded,new string[0],hostLinks:false));
                Check(run.Stats.ExcludedBeforeIndex==2 && run.Stats.IndexedElements==0,"Support and unmapped generic elements are excluded before the index");
                Check(run.Stats.GeometryLoads==0 && run.Stats.BooleanTests==0 && run.Rows.Count==0,"Excluded-only model performs no solid extraction or Boolean operations");
                evidence.Add(new {scenario="excluded-only",statistics=run.Stats});
            } finally {excluded.Close(false);}

            string linkPath=Path.Combine(_folder,"scan-planning-link.rvt");
            var linked=app.Application.NewProjectDocument(UnitSystem.Metric);
            try {
                using(var tx=new Transaction(linked,"Scan planning linked fixture")){tx.Start();AddElements(linked);tx.Commit();}
                linked.SaveAs(linkPath,new SaveAsOptions {OverwriteExistingFile=true});
            } finally {linked.Close(false);}
            var host=app.Application.NewProjectDocument(UnitSystem.Metric);
            try {
                ElementId genericId=ElementId.InvalidElementId,floorId=ElementId.InvalidElementId,insulationId=ElementId.InvalidElementId;
                Pipe pipe=null!;Level upper=null!;
                using(var tx=new Transaction(host,"Scan planning host fixture")) {
                    tx.Start();AddElements(host);
                    genericId=new FilteredElementCollector(host).OfCategory(BuiltInCategory.OST_GenericModel).WhereElementIsNotElementType().FirstElementId();
                    floorId=TypedBox(host,BuiltInCategory.OST_Floors,"FLOOR-01",XYZ.Zero).Id;
                    var level=Level.Create(host,0);upper=Level.Create(host,20);
                    var system=PipingSystemType.Create(host,MEPSystemClassification.Sanitary,"SAN-01 coordination fixture");
                    var pipeType=new FilteredElementCollector(host).OfClass(typeof(PipeType)).FirstElement();
                    pipe=Pipe.Create(host,system.Id,pipeType.Id,level.Id,new XYZ(100,0,3),new XYZ(110,0,3));
                    var insulationType=new FilteredElementCollector(host).OfClass(typeof(PipeInsulationType)).FirstElement();
                    if(insulationType!=null)insulationId=PipeInsulation.Create(host,pipe.Id,insulationType.Id,0.1).Id;
                    var type=RevitLinkType.Create(host,ModelPathUtils.ConvertUserVisiblePathToModelPath(linkPath),new RevitLinkOptions(false));
                    RevitLinkInstance.Create(host,type.ElementId);
                    var shifted=RevitLinkInstance.Create(host,type.ElementId);ElementTransformUtils.MoveElement(host,shifted.Id,new XYZ(.25,0,0));
                    var rotated=RevitLinkInstance.Create(host,type.ElementId);ElementTransformUtils.RotateElement(host,rotated.Id,Line.CreateBound(XYZ.Zero,XYZ.BasisZ),Math.PI/4);
                    tx.Commit();
                }
                var links=new LinkedModelManager(host).GetLoadedLinks();
                var ids=links.Select(l=>l.Instance.UniqueId).OrderBy(id=>id,StringComparer.Ordinal).ToArray();
                Check(ids.Length==3,"Fixture has three distinct repeated and transformed link instances");
                var plan=Plan(host,ids);
                var fullPreferences=new AppSettings {IncludeGenericModels=true,IncludeInsulation=true,ExcludeNamedSupports=false};
                var factoryPlan=RevitScanPlanFactory.Create(host,fullPreferences,links,true);
                Check(!factoryPlan.CandidatePolicy.IncludeGenericModels && !factoryPlan.CandidatePolicy.IncludeInsulation && factoryPlan.CandidatePolicy.ExcludeNamedSupports,
                    "Native plan factory uses Full Scan preferences independently of Live candidate settings");
                fullPreferences.FullScanIncludeGenericModels=true;
                Check(!factoryPlan.CandidatePolicy.IncludeGenericModels,"Changing application preferences cannot mutate an already frozen plan");
                var invalidLevel=Plan(host,ids,level:long.MaxValue.ToString());
                bool rejected=false;
                try {using var invalid=new ClashResolveAI.ClashEngine.ClashEngine(host).CreatePlannedJob(invalidLevel);}
                catch(ArgumentException){rejected=true;}
                Check(rejected,"Missing floor selection is rejected instead of broadening to the whole model");
                var options=new Commands.FullScanOptionsDialog(new AppSettings(),"Fixture floor","Three loaded instances",
                    links.Select(l=>new Commands.ScanLinkChoice(l.Instance.UniqueId,l.FileName+" ["+l.Instance.Id.Value+"]")));
                Check(options.SelectedLinkIds.Count==3 && options.SelectedLinkPairs.Count==0 && !options.Options.IncludeLinkToLink,
                    "Existing Full Scan dialog exposes selected instances and starts without automatic link pairs");
                options.Options.InsideLinks.IsChecked=true;
                var optionsCopy=new AppSettings();options.Options.Apply(optionsCopy,false);
                Check(optionsCopy.FullScanWithinLinks && !optionsCopy.IncludeLinkToLink && optionsCopy.LiveMode==ScanMode.HardOnly,
                    "Scan controls persist independent internal-link selection without changing Live mode");
                options.Close();
                var classifier=new RevitElementClassifier(plan);
                var sanitary=classifier.Get(pipe);
                Check(sanitary.Discipline==Discipline.GravityDrainage && sanitary.Evidence==ClassificationEvidence.RevitSystemClassification && sanitary.Confidence>0.9,
                    "Native SAN-01 pipe uses Revit system classification with high confidence");
                var shared=links[0].LinkDoc.GetElement(new FilteredElementCollector(links[0].LinkDoc).OfCategory(BuiltInCategory.OST_MechanicalEquipment).WhereElementIsNotElementType().FirstElementId());
                var cached=classifier.Get(shared);int classified=classifier.ClassificationCount;
                var repeated=links[1].LinkDoc.GetElement(shared.Id);
                Check(object.ReferenceEquals(cached,classifier.Get(repeated)) && classifier.ClassificationCount==classified,"Repeated instances share one document-element classification record");
                var generic=classifier.Get(host.GetElement(genericId));
                Check(generic.Discipline==Discipline.Unknown && !generic.IsCoordinationCandidate,"Native medical outlet generic remains Unknown without an explicit mapping");
                var normal=Run(host,plan);
                Check(normal.Stats.CoversWholeHostScope,"Complete host coordination retains full-scan freshness certification");
                Check(normal.Rows.Count>0 && normal.Rows.All(c=>c.LinkInstanceA=="" || c.LinkInstanceB==""),"Default native scan produces host coordination with no linked-only pairs");
                Check(normal.Stats.IndexedElements==9 && normal.Stats.ClassifiedElements==9 && normal.Stats.ExcludedBeforeIndex==8,
                    "Three link indexes reuse classification while excluding all supports and unmapped generics");
                Check(normal.Rows.All(c=>c.ElementAId!=genericId.Value || c.LinkInstanceA!="") && normal.Rows.All(c=>c.ElementBId!=genericId.Value || c.LinkInstanceB!=""),"Excluded generic model never reaches physical results");
                Check(normal.Rows.All(c=>!(c.LinkInstanceA==""&&c.ElementAId==floorId.Value) && !(c.LinkInstanceB==""&&c.ElementBId==floorId.Value)),"Floor is omitted when the selected rule policy does not require it");
                evidence.Add(new {scenario="default",statistics=normal.Stats,results=normal.Rows.Select(c=>new {c.ClashId,c.LinkInstanceA,c.LinkInstanceB})});
                var native=Run(host,plan,true);
                Check(new HashSet<string>(normal.Rows.Select(c=>c.NormalizedKey)).SetEquals(native.Rows.Select(c=>c.NormalizedKey)),"Native bounding-box query path agrees with indexed host coordination");
                var inside=Run(host,Plan(host,new[]{ids[0]},host:false,hostLinks:false,inside:true));
                Check(inside.Rows.Count==1 && inside.Rows.All(c=>c.LinkInstanceA==ids[0] && c.LinkInstanceB==ids[0]),"Internal-link job runs independently with link-to-link disabled");
                var cross=Run(host,Plan(host,ids,host:false,hostLinks:false,cross:true,pairs:new[]{new ScanModelPair(ids[0],ids[1])}));
                Check(cross.Rows.Count>0 && cross.Rows.All(c=>new ScanModelPair(ids[0],ids[1]).Matches(c.LinkInstanceA,c.LinkInstanceB)),"Cross-link job tests only the explicitly selected pair");
                Check(cross.Stats.IndexedElements==4 && !cross.Stats.CoversWholeHostScope,"Cross-link-only job indexes neither the host nor the unselected third instance");
                var mapping=new ClassificationMapping("medical-outlet",Discipline.MedicalGas,"MEDICAL OUTLET");
                var mapped=Run(host,Plan(host,ids,mappings:new[]{mapping}));
                Check(mapped.Stats.IndexedElements==normal.Stats.IndexedElements+4,"Explicit mapping admits one outlet per participating instance and host");
                var reusedEngine=new ClashResolveAI.ClashEngine.ClashEngine(host);
                using(var first=reusedEngine.CreatePlannedJob(plan)){while(!first.Advance(25)) { }}
                using(var second=reusedEngine.CreatePlannedJob(Plan(host,ids,mappings:new[]{mapping}))) {
                    while(!second.Advance(25)) { }
                    Check(second.Statistics.IndexedElements==mapped.Stats.IndexedElements,"A reused engine discards classifications when a new plan changes mappings");
                }
                var required=Run(host,Plan(host,new string[0],hostLinks:false,policy:new CoordinationCandidatePolicy(requiredEnvelopeCategoryIds:new[]{(long)BuiltInCategory.OST_Floors})));
                Check(required.Rows.Any(c=>c.ElementAId==floorId.Value || c.ElementBId==floorId.Value),"Rule-required floor participates in native overlap tests");
                var emptyFloor=Run(host,Plan(host,ids,level:upper.Id.Value.ToString()));
                Check(emptyFloor.Rows.Count==0 && !emptyFloor.Stats.CoversWholeHostScope,"Upper-floor scope does not evaluate lower-floor sources or certify whole-host coverage");
                if(insulationId!=ElementId.InvalidElementId) {
                    var insulation=classifier.Get(host.GetElement(insulationId));
                    Check(!insulation.IsCoordinationCandidate,"Native insulation is rejected from the hard-clash pass");
                    var dedicated=new RevitElementClassifier(Plan(host,new string[0],hostLinks:false,stage:ScanStage.InsulationClearance));
                    Check(dedicated.Get(host.GetElement(insulationId)).IsCoordinationCandidate,"Native insulation is admitted only in the dedicated clearance stage");
                }
                else File.WriteAllText(Path.Combine(_folder,"insulation-fixture-note.txt"),"The stock metric fixture has no PipeInsulationType. Dedicated insulation policy is covered by automated policy checks; native insulation geometry remains for the later staged-clearance phase.");
                using(var tx=new Transaction(host,"Move link after first scan")){tx.Start();ElementTransformUtils.MoveElement(host,links[0].Instance.Id,new XYZ(500,0,0));tx.Commit();}
                var refreshed=Run(host,Plan(host,new[]{links[0].Instance.UniqueId},host:false));
                Check(refreshed.Rows.Count==0,"New job refreshes link transforms rather than reusing old spatial snapshots");
                evidence.Add(new {scenario="selected-pair",statistics=cross.Stats});
                Check(normal.Stats.ScanCaptureJson.Contains("ScanPlanningFixture") && normal.Stats.ScanPlanJson.Contains(ids[0]),"Native scan capture records frozen rules and instance selections");
                File.WriteAllText(Path.Combine(_folder,"scan-planning-evidence.json"),JsonConvert.SerializeObject(evidence,Formatting.Indented));
            } finally {host.Close(false);}
        }
    }
}
