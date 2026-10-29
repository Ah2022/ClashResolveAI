using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.ClashEngine;
using ClashResolveAI.Commands;
using ClashResolveAI.Rules;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ClashResolveAI.Core
{
    internal static partial class IntegrationVerification
    {
        private static void VerifyScanFilteringNative(UIApplication app)
        {
            DirectShape Typed(Document doc,BuiltInCategory category,string name,XYZ point) {
                var shape=Box(doc,category,point);shape.SetTypeId(DirectShapeType.Create(doc,name,new ElementId((long)category)).Id);return shape;
            }
            var genericCategory=(long)BuiltInCategory.OST_GenericModel;var electricalCategory=(long)BuiltInCategory.OST_ElectricalEquipment;
            var defaultRules=new RuleSet {Name="FilteringFixture",Rules=new Dictionary<string,ClashRule>{{"Generic",new ClashRule {ClearanceMM=50}}}};
            ScanPlan Plan(Document doc,RuleSet? rules=null,IEnumerable<ScanDisciplinePair>? pairs=null,IEnumerable<long>? cats=null,
                IEnumerable<string>? clearance=null,ScanMode mode=ScanMode.HardOnly,string level="") =>
                new ScanPlan(DocumentSession.Key(doc),ScanProfile.Advanced,mode==ScanMode.HardOnly?ScanStage.HardClash:ScanStage.Coordination,mode,
                    true,false,false,false,Array.Empty<string>(),levelIds:level==""?null:new[]{level},disciplinePairs:pairs,categoryIds:cats,clearanceRuleKeys:clearance,
                    ruleSetContent:JsonConvert.SerializeObject(rules??defaultRules),candidatePolicy:new CoordinationCandidatePolicy(includeGenericModels:true),
                    mappings:new[]{new ClassificationMapping("medical",Discipline.MedicalGas,"MEDICAL OUTLET")},medicalGasKeywords:new[]{"MEDICAL OUTLET"});
            (List<ClashResult> Rows,ScanStatistics Stats) Run(Document doc,ScanPlan plan) {
                using var job=new ClashResolveAI.ClashEngine.ClashEngine(doc).CreatePlannedJob(plan);while(!job.Advance(25)) { }return (job.Results,job.Statistics);
            }
            var doc=app.Application.NewProjectDocument(UnitSystem.Metric);
            try {
                DirectShape outlet=null!,electrical=null!;
                using(var tx=new Transaction(doc,"Partition-filter fixture")) {
                    tx.Start();outlet=Typed(doc,BuiltInCategory.OST_GenericModel,"MEDICAL OUTLET",XYZ.Zero);
                    electrical=Typed(doc,BuiltInCategory.OST_ElectricalEquipment,"PANEL-01",new XYZ(.5,.5,0));tx.Commit();
                }
                var unrequested=Run(doc,Plan(doc));
                Check(unrequested.Rows.Count==0&&unrequested.Stats.GeometryLoads==0&&unrequested.Stats.BooleanTests==0,
                    "Unsupported native medical/electrical overlap performs no geometry or Boolean operations");
                Check(unrequested.Stats.PartitionRuleChecks==4&&unrequested.Stats.RejectedPartitionChecks==2&&unrequested.Stats.SpatialQueries==2,
                    "Unsupported cross-discipline groups receive no spatial queries; only the two self partitions are queried");
                var overrides=JsonConvert.DeserializeObject<RuleSet>(JsonConvert.SerializeObject(defaultRules))!;
                overrides.CategoryPairs[genericCategory+"|"+electricalCategory]=new ClashRule {Severity="Critical",ClearanceMM=75};
                var positive=Run(doc,Plan(doc,overrides));
                Check(positive.Rows.Count==1&&positive.Rows[0].Severity==ClashSeverity.Critical&&positive.Rows[0].RuleApplied.Contains("CatPair:"),
                    "Explicit category override recovers a valid pair and records the resolved rule identity");
                Check(!unrequested.Stats.Scope.Contains(positive.Rows[0]),"Rejected partition coverage cannot resolve a previously observed native pair");
                var selected=Run(doc,Plan(doc,pairs:new[]{new ScanDisciplinePair(Discipline.MedicalGas,Discipline.Electrical)}));
                Check(selected.Rows.Count==1&&selected.Stats.SpatialQueries==2,"Explicit advanced discipline selection queries only the requested cross-discipline pair");
                var categoryOnly=Run(doc,Plan(doc,cats:new[]{genericCategory}));
                Check(categoryOnly.Rows.Count==0&&categoryOnly.Stats.IndexedElements==1,"Category restriction removes unselected categories before indexing");
                overrides.CategoryPairs[genericCategory+"|"+electricalCategory]=new ClashRule {Ignore=true};
                var ignored=Run(doc,Plan(doc,overrides));
                Check(ignored.Rows.Count==0&&ignored.Stats.GeometryLoads==0&&ignored.Stats.SpatialQueries==2,"Ignored category pair is pruned before native spatial retrieval");

                var store=new ProjectClassificationStore();int savedNotifications=0;Exception? uiFailure=null;
                var review=new ClassificationReviewWindow(Plan(doc),store,()=>RevitClassificationInventory.Collect(doc,Plan(doc)).GetEnumerator(),()=>savedNotifications++);
                var observer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)};var timeout=DateTime.UtcNow.AddSeconds(30);
                observer.Tick+=(_,__)=> {
                    if(!review.InventoryReady&&DateTime.UtcNow<timeout)return;
                    observer.Stop();
                    try {
                        Check(review.InventoryReady&&review.Model.ElementCount==2,"Modal review collects real Revit metadata in bounded API-thread slices");
                        var row=review.Model.Rows.Single(r=>r.CategoryId==genericCategory);
                        Check(row.CurrentClassification=="Unknown"&&row.SuggestedClassification.Contains("MedicalGas"),"Native review keeps generic classification Unknown until coordinator confirmation");
                        CaptureLifecycleUi(review,"classification-review.png");
                        review.Model.Confirm(row);
                        var save=Descendants<Button>(review).Single(b=>b.Content as string=="Save project mappings");
                        Check(save.IsEnabled,"Reviewed draft enables the project save action");save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(savedNotifications==1&&!review.Model.Dirty&&store.Load(DocumentSession.Key(doc)).Mappings.Count==1,"Native save persists mappings and publishes the invalidation callback");
                        var editor=new ClassificationMappingEditor(review.Model.Rows,review.Model.Mappings.Single(),null) {Owner=review};
                        editor.Show();CaptureLifecycleUi(editor,"classification-mapping-editor.png");editor.Close();
                        var factory=RevitScanPlanFactory.Create(doc,new AppSettings {FullScanIncludeGenericModels=true},Array.Empty<ClashResolveAI.Links.LinkedModel>(),false);
                        Check(factory.ProjectMappingsFingerprint==store.Load(DocumentSession.Key(doc)).Fingerprint&&factory.Mappings.Count==1,
                            "New scan plan automatically freezes persisted project mappings and their content fingerprint");
                        Check(new RevitElementClassifier(factory).Get(outlet).Discipline==Discipline.MedicalGas,"Persisted review confirmation is used during subsequent native collection");
                    } catch(Exception ex){uiFailure=ex;review.Model.Reload();}
                    finally {review.Close();}
                };
                observer.Start();review.ShowDialog();observer.Stop();if(uiFailure!=null)throw uiFailure;
                var boundPlan=RevitScanPlanFactory.Create(doc,new AppSettings {FullScanIncludeGenericModels=true},Array.Empty<ClashResolveAI.Links.LinkedModel>(),false);
                var pending=new ClashResolveAI.ClashEngine.ClashEngine(doc).CreatePlannedJob(boundPlan);string cancellation="";
                ScanCoordinator.StartJob(doc,pending,(_,__)=>{},failed:reason=>cancellation=reason);
                ScanCoordinator.MappingsChanged(DocumentSession.Key(doc));
                Check(!ScanCoordinator.Busy&&pending.Cancelled&&cancellation.Contains("classification mappings changed"),"Saving mappings cancels an active Full Scan before it can publish stale classification results");
                File.WriteAllText(Path.Combine(_folder,"partition-filter-evidence.json"),JsonConvert.SerializeObject(new {unsupported=unrequested.Stats,positive=positive.Stats,selected=selected.Stats},Formatting.Indented));
            } finally {doc.Close(false);}

            var clearanceDoc=app.Application.NewProjectDocument(UnitSystem.Metric);
            try {
                using(var tx=new Transaction(clearanceDoc,"Selective clearance fixture")) {
                    tx.Start();Typed(clearanceDoc,BuiltInCategory.OST_MechanicalEquipment,"AHU-A",XYZ.Zero);
                    Typed(clearanceDoc,BuiltInCategory.OST_StructuralColumns,"COLUMN",new XYZ(2.25,0,0));
                    Typed(clearanceDoc,BuiltInCategory.OST_MechanicalEquipment,"AHU-B",new XYZ(20,0,0));
                    Typed(clearanceDoc,BuiltInCategory.OST_MechanicalEquipment,"AHU-C",new XYZ(20.5,0,0));
                    Typed(clearanceDoc,BuiltInCategory.OST_MechanicalEquipment,"AHU-D",new XYZ(40,0,0));
                    Typed(clearanceDoc,BuiltInCategory.OST_MechanicalEquipment,"AHU-E",new XYZ(42.25,0,0));tx.Commit();
                }
                var rules=new RuleSet {Name="SelectiveClearance",Rules=new Dictionary<string,ClashRule>{{"Generic",new ClashRule {ClearanceMM=500}},
                    {"Duct_vs_Beam",new ClashRule {ClearanceMM=100}}}};
                var result=Run(clearanceDoc,Plan(clearanceDoc,rules,clearance:new[]{"Duct_vs_Beam"},mode:ScanMode.HardAndClearance));
                Check(result.Rows.Count(c=>c.TestType==ClashTestType.HardClash)==1&&result.Rows.Count(c=>c.TestType==ClashTestType.ClearanceClash)==1,
                    "Selected clearance rules retain unrelated valid hard clashes and omit unselected clearance-only findings");
                Check(Math.Abs(result.Rows.Single(c=>c.TestType==ClashTestType.ClearanceClash).GapMM-76.2)<.1,"Selected clearance still measures the native 76.2 mm planar gap");
            } finally {clearanceDoc.Close(false);}

            var boundary=app.Application.NewProjectDocument(UnitSystem.Metric);
            try {
                Level lower=null!;
                using(var tx=new Transaction(boundary,"Cross-floor source fixture")) {
                    tx.Start();lower=Level.Create(boundary,0);Level.Create(boundary,10);
                    Typed(boundary,BuiltInCategory.OST_MechanicalEquipment,"CROSS-FLOOR AHU",new XYZ(0,0,9));
                    Typed(boundary,BuiltInCategory.OST_StructuralColumns,"NEARBY ABOVE FLOOR",new XYZ(0,0,11.25));tx.Commit();
                }
                var rules=new RuleSet {Name="BoundaryClearance",Rules=new Dictionary<string,ClashRule>{{"Generic",new ClashRule {ClearanceMM=50}},
                    {"Duct_vs_Beam",new ClashRule {ClearanceMM=100}}}};
                var result=Run(boundary,Plan(boundary,rules,mode:ScanMode.HardAndClearance,level:lower.Id.Value.ToString()));
                Check(result.Rows.Count==1&&result.Rows[0].TestType==ClashTestType.ClearanceClash,"Scope pruning retains a target beyond the floor band beside a boundary-crossing source");
            } finally {boundary.Close(false);}
        }
    }
}
