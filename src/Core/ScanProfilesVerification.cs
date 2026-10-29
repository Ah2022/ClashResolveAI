using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Commands;
using ClashResolveAI.Links;
using ClashResolveAI.LiveMonitor;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ComboBox=System.Windows.Controls.ComboBox;

namespace ClashResolveAI.Core
{
    internal static partial class IntegrationVerification
    {
        private static void VerifyScanProfilesNative(UIApplication app)
        {
            Check(app.ActiveUIDocument==null,"Estimate verification starts without an open user document");
            DirectShape Typed(Document doc,BuiltInCategory category,string name,XYZ point) {
                var shape=Box(doc,category,point);shape.SetTypeId(DirectShapeType.Create(doc,name,new ElementId((long)category)).Id);return shape;
            }
            var linked=app.Application.NewProjectDocument(UnitSystem.Metric);
            string linkPath=Path.Combine(_folder,"estimate-link.rvt");
            using(var tx=new Transaction(linked,"Estimate linked fixture")){tx.Start();Typed(linked,BuiltInCategory.OST_MechanicalEquipment,"AHU-LINK",XYZ.Zero);Typed(linked,BuiltInCategory.OST_StructuralColumns,"COLUMN-LINK",new XYZ(.5,.5,0));tx.Commit();}
            linked.SaveAs(linkPath);linked.Close(false);
            var fixture=app.Application.NewProjectDocument(UnitSystem.Metric);ElementId moving=ElementId.InvalidElementId;
            using(var tx=new Transaction(fixture,"Estimate host fixture")) {
                tx.Start();moving=Typed(fixture,BuiltInCategory.OST_MechanicalEquipment,"AHU-HOST",XYZ.Zero).Id;
                Typed(fixture,BuiltInCategory.OST_StructuralColumns,"COLUMN-HOST",new XYZ(.5,.5,0));
                Typed(fixture,BuiltInCategory.OST_MechanicalEquipment,"HANGER M10",XYZ.Zero);
                Typed(fixture,BuiltInCategory.OST_GenericModel,"MEDICAL OUTLET",XYZ.Zero);
                var level=Level.Create(fixture,0);Level.Create(fixture,30);
                var floorType=new FilteredElementCollector(fixture).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.FloorPlan);
                ViewPlan.Create(fixture,floorType.Id,level.Id);
                var loaded=RevitLinkType.Create(fixture,ModelPathUtils.ConvertUserVisiblePathToModelPath(linkPath),new RevitLinkOptions(false));
                RevitLinkInstance.Create(fixture,loaded.ElementId);var repeated=RevitLinkInstance.Create(fixture,loaded.ElementId);
                ElementTransformUtils.MoveElement(fixture,repeated.Id,new XYZ(.25,.25,0));tx.Commit();
            }
            string path=Path.Combine(_folder,"estimate-host.rvt");fixture.SaveAs(path);fixture.Close(false);
            var doc=app.OpenAndActivateDocument(path).Document;DocumentSession.Activate(doc);
            var preferences=AppSettings.Load().ScanSnapshot();preferences.MigrateScanConfiguration();preferences.FullScanLinkedModels=true;
            preferences.FullScanRuleSetName="DefaultRules";preferences.FullScanMinimumOverlapMM3=1;
            AppSettings.Save(preferences);string settingsBefore=JsonConvert.SerializeObject(AppSettings.Load());
            var evidence=new List<object>();ScanPlan? approved=null;ScanEstimate? reviewed=null;Exception? failure=null;
            var wizard=new ScanProfilesDialog(app,preferences,p=>RevitScanEstimate.Create(doc,p,Array.Empty<ScanTimingObservation>()));
            int state=0;var observer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)};var deadline=DateTime.UtcNow.AddSeconds(75);
            Check(wizard.SelectedProfile==ScanProfile.QuickCoordination&&!wizard.CanStart,"Actual profile dialog defaults to Quick and gates Start before estimation");
            var initial=wizard.BuildPlan();
            Check(initial.Mode==ScanMode.HardOnly&&initial.LevelIds.Count==1&&initial.Relationships().Count==3&&!initial.InsideLinks&&!initial.LinkToLink,"Quick UI captures one floor and two selected loaded link instances");
            bool rejected=false;try{wizard.AcceptEstimate(false);}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"Native Start gate rejects an unestimated scan");
            observer.Tick+=(_,__)=> {
                try {
                    if(DateTime.UtcNow>deadline)throw new TimeoutException("Profile dialog estimate did not complete.");
                    var tabs=Descendants<TabControl>(wizard).Single();
                    if(state==0){CaptureLifecycleUi(wizard,"profile-quick-scope.png");wizard.CalculateEstimate();state=1;return;}
                    if(wizard.CurrentEstimate==null)return;
                    if(state==1) {
                        var estimate=wizard.CurrentEstimate;
                        Check(wizard.CanStart&&estimate.Pairs.Count==3&&estimate.Pairs.All(p=>p.Complete),"Native bounded estimate completes small metadata work and enables review");
                        Check(estimate.Models.Count(m=>m.Id!=""&&m.Loaded)==2&&estimate.Models.All(m=>m.Elements>0),"Inventory reports loaded links and native non-type element counts per instance");
                        Check(estimate.Models.Single(m=>m.Id=="").Excluded>=1&&estimate.SourceElements==2,"Estimate excludes support/generic geometry and reports scoped host sources");
                        Check(estimate.DurationLowSeconds==null&&estimate.Pairs.All(p=>p.DurationLowSeconds==null),"Native estimate honestly reports unavailable uncalibrated duration");
                        Check(!doc.IsModified,"Metadata estimation leaves the saved Revit model unmodified");
                        Check(wizard.AcceptEstimate(false)&&ScanProfiles.Fingerprint(wizard.ApprovedPlan!)==estimate.PlanFingerprint,"Start approves the exact frozen estimated plan");
                        approved=wizard.ApprovedPlan;reviewed=estimate;evidence.Add(new {scenario="quick",plan=approved,estimate});
                        CaptureLifecycleUi(wizard,"profile-quick-estimate.png");
                        var workload=Descendants<DataGrid>(wizard).Single(g=>g.Columns.Any(c=>Equals(c.Header,"Source relationships")));
                        var row=workload.ItemsSource.Cast<WorkloadPairRow>().First(r=>r.Pair.Target!="");row.Included=false;
                        Check(!wizard.CanStart&&wizard.CurrentEstimate==null,"Removing a relationship immediately invalidates the review gate");
                        wizard.CalculateEstimate();state=2;return;
                    }
                    if(state==2) {
                        Check(wizard.CurrentEstimate.Pairs.Count==2&&wizard.BuildPlan().ExcludedRelationships.Count==1,"Re-estimate removes the expensive relationship from executable totals");
                        wizard.SelectedProfile=ScanProfile.DetailedCoordination;var detailed=wizard.BuildPlan();
                        Check(detailed.Mode==ScanMode.HardAndClearance&&detailed.ClearanceRuleKeys.Count>0&&!detailed.LinkToLink,"Detailed UI requires named clearance rules and keeps link-to-link off");
                        tabs.SelectedIndex=2;CaptureLifecycleUi(wizard,"profile-detailed-rules.png");wizard.CalculateEstimate();state=3;return;
                    }
                    if(state==3) {
                        Check(wizard.CanStart&&wizard.CurrentEstimate.Pairs.Any(p=>p.ClearancePartitions>0),"Detailed estimate verifies selected rules apply to actual classified partitions");
                        wizard.SelectedProfile=ScanProfile.Advanced;tabs.SelectedIndex=1;
                        Descendants<CheckBox>(wizard).Single(c=>(c.Content as string)?.StartsWith("Link ↔ link — separate job")==true).IsChecked=true;
                        var button=Descendants<Button>(wizard).Single(b=>Equals(b.Content,"Add selected pair"));button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var cross=wizard.BuildPlan();
                        Check(cross.LinkPairs.Count==1&&cross.Relationships().Count==1&&!cross.HostToHost&&!cross.HostToLinks&&!cross.InsideLinks,"Advanced UI schedules only its explicit cross-link pair as a separate job");
                        CaptureLifecycleUi(wizard,"profile-advanced-models.png");wizard.CalculateEstimate();state=4;return;
                    }
                    if(state==4) {
                        Check(wizard.CanStart&&wizard.CurrentEstimate.Pairs.Count==1&&wizard.CurrentEstimate.Warnings.Any(w=>w.Contains("Link-to-link")),"Advanced estimate exposes the explicit link-pair warning and workload");
                        evidence.Add(new {scenario="cross-link",plan=wizard.BuildPlan(),estimate=wizard.CurrentEstimate});CaptureLifecycleUi(wizard,"profile-advanced-estimate.png");
                        Descendants<CheckBox>(wizard).Single(c=>(c.Content as string)?.StartsWith("Insulation clearance — dedicated")==true).IsChecked=true;
                        var insulation=wizard.BuildPlan();Check(insulation.Stage==ScanStage.InsulationClearance&&insulation.CandidatePolicy.IncludeInsulation&&!insulation.LinkToLink,"Changing Advanced job switches cleanly from cross-link to dedicated insulation");
                        wizard.SelectedProfile=ScanProfile.QuickCoordination;wizard.CalculateEstimate();state=5;return;
                    }
                    if(state==5) {
                        using(var tx=new Transaction(doc,"Change fixture after estimate")){tx.Start();ElementTransformUtils.MoveElement(doc,moving,new XYZ(10,0,0));tx.Commit();}
                        bool staleRejected=false;try{wizard.AcceptEstimate(false);}catch(InvalidOperationException){staleRejected=true;}
                        Check(staleRejected&&!wizard.CanStart,"Model edit after estimation prevents starting with stale workload approval");
                        wizard.CalculateEstimate();state=6;return;
                    }
                    if(state==6) {
                        Check(wizard.AcceptEstimate(false),"A fresh estimate can be approved after the model changes");
                        approved=wizard.ApprovedPlan;reviewed=wizard.CurrentEstimate;
                        observer.Stop();wizard.Close();
                    }
                } catch(Exception ex){failure=ex;observer.Stop();wizard.Close();}
            };
            observer.Start();wizard.ShowDialog();observer.Stop();if(failure!=null)throw failure;
            Check(approved!=null&&reviewed!=null,"Native modal workflow returns a reviewed immutable plan");
            var engine=new ClashResolveAI.ClashEngine.ClashEngine(doc);
            using(var job=engine.CreatePlannedJob(approved!)) {
                while(!job.Advance(25)){}
                Check(job.Statistics.Candidates==reviewed!.BoundingBoxPairs,"Complete native estimate matches executed bounding-box candidate count");
                Check(job.Statistics.SpatialQueries==reviewed.Pairs.Sum(p=>p.SpatialQueries),"Estimate and execution use identical pre-geometry partition queries");
                Check(job.Statistics.GeometryLoads>0&&job.Statistics.BooleanTests>0,"Reviewed scan still performs actual Revit solids and Boolean verification");
                evidence.Add(new {scenario="reviewed-execution",plan=approved,estimate=reviewed,statistics=job.Statistics});
            }
            var beforeApproval=LiveEnvironment.Capture(doc);
            var fresh=AppSettings.Load().ScanSnapshot();ScanProfilesDialog.SaveFullPreferences(fresh,approved!);AppSettings.Save(fresh);
            Check(LiveEnvironment.Capture(doc)==beforeApproval,"Saving the approved profile preserves the actual Live Monitor environment hash");
            var liveAfterModelChange=LiveEnvironment.Capture(doc);
            fresh.FullScanRuleSetName="FullOnly-Verification";fresh.FullScanWithinLinks=true;fresh.FullScanLinkedModels=false;
            fresh.FullScanIncludeGenericModels=true;fresh.FullScanIncludeInsulation=true;fresh.FullScanMinimumOverlapMM3=999;fresh.FullScanMode=ScanMode.HardAndClearance;
            AppSettings.Save(fresh);
            Check(LiveEnvironment.Capture(doc)==liveAfterModelChange,"Changing Full Scan rules, candidates, relationships and tolerances leaves actual Live Monitor freshness unchanged");
            var beforeSettings=JsonConvert.DeserializeObject<AppSettings>(settingsBefore)!;
            Check(fresh.ScanWithinLinks==beforeSettings.ScanWithinLinks&&fresh.ScanLinkedModels==beforeSettings.ScanLinkedModels&&fresh.IncludeGenericModels==beforeSettings.IncludeGenericModels&&fresh.IncludeInsulation==beforeSettings.IncludeInsulation&&fresh.RuleSetName==beforeSettings.RuleSetName&&fresh.MinimumOverlapMM3==beforeSettings.MinimumOverlapMM3,"Profile approval preserves persisted Live Monitor preferences");
            AppSettings.Save(preferences);
            int observed=0;string message="";Result result;
            ScanProfilesDialog.VerificationOpened=open=>{observed++;Check(!open.CanStart,"Full Scan command opens the new gated profile workflow");open.Close();};
            try{result=DetectClashesCommand.Run(app,ref message);}finally{ScanProfilesDialog.VerificationOpened=null;}
            Check(observed==1&&result==Result.Cancelled&&!ScanCoordinator.Busy,$"Cancelling the actual Full Scan command starts no geometry job (observed={observed}; result={result}; {message})");
            File.WriteAllText(Path.Combine(_folder,"scan-estimate-evidence.json"),JsonConvert.SerializeObject(evidence,Formatting.Indented));
            doc.Save(); // Disposable fixture only; permits clean process closure without a save prompt.
        }
    }
}
