using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Commands;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClashResolveAI.Core
{
    internal static partial class IntegrationVerification
    {
        private static void VerifyHostScope(UIApplication app)
        {
            var settings=AppSettings.Load().ScanSnapshot();
            try {
                var defaults=new AppSettings();
                Check(!defaults.IncludeLinkToLink,"Link-to-link defaults off for host-centric scans");
                var window=new SettingsWindow();
                var controls=Descendants<ScanOptionsPanel>(window).Single();
                controls.FullMode.SelectedIndex=1;controls.LiveMode.SelectedIndex=1;controls.LinkToLink.IsChecked=true;
                window.SaveValues();window.Close();
                var persisted=AppSettings.Load();
                Check(persisted.FullScanMode==ScanMode.HardAndClearance&&persisted.LiveMode==ScanMode.HardAndClearance&&persisted.IncludeLinkToLink,"Settings window saves and reloads both scan modes and link-to-link");
                var second=new SettingsWindow();var choices=Descendants<ScanOptionsPanel>(second).Single();
                choices.FullMode.SelectedIndex=0;choices.LiveMode.SelectedIndex=0;choices.LinkToLink.IsChecked=false;second.SaveValues();second.Close();
                persisted=AppSettings.Load();
                Check(persisted.FullScanMode==ScanMode.HardOnly&&persisted.LiveMode==ScanMode.HardOnly&&!persisted.IncludeLinkToLink,"Settings window also persists hard-only and unchecked link-to-link");
                var dialog=new FullScanOptionsDialog(persisted,"All Floors","Verification fixture: two loaded link instances");
                dialog.Show();dialog.UpdateLayout();
                Check(dialog.Options.SelectedMode==ScanMode.HardOnly&&!dialog.Options.IncludeLinkToLink,"Full Scan dialog reflects saved host-centric defaults");
                dialog.Options.FullMode.SelectedIndex=1;dialog.Options.LinkToLink.IsChecked=true;
                var copy=persisted.ScanSnapshot();dialog.Options.Apply(copy,false);
                Check(copy.FullScanMode==ScanMode.HardAndClearance&&copy.IncludeLinkToLink&&copy.LiveMode==persisted.LiveMode,"Pre-scan choices persist without changing Live Monitor mode");
                CaptureLifecycleUi(dialog,"phase3-scan-options.png");dialog.Close();
                var disabled=new ScanOptionsPanel(new AppSettings {ScanLinkedModels=false,IncludeLinkToLink=true},false);
                Check(!disabled.IncludeLinkToLink&&!disabled.LinkToLink.IsEnabled,"Link-to-link cannot run when linked-model scanning is disabled");

                string path=Path.Combine(_folder,"phase3-link.rvt");
                var linked=app.Application.NewProjectDocument(UnitSystem.Metric);
                using(var tx=new Transaction(linked,"Host-scope linked fixture")){
                    tx.Start();Box(linked,BuiltInCategory.OST_MechanicalEquipment,XYZ.Zero);
                    Box(linked,BuiltInCategory.OST_StructuralColumns,new XYZ(.5,.5,0));tx.Commit();
                }
                linked.SaveAs(path,new SaveAsOptions {OverwriteExistingFile=true});linked.Close(false);
                var doc=app.Application.NewProjectDocument(UnitSystem.Metric);
                try {
                    using(var tx=new Transaction(doc,"Host-scope host fixture")){
                        tx.Start();Box(doc,BuiltInCategory.OST_MechanicalEquipment,new XYZ(.25,.25,0));
                        var type=RevitLinkType.Create(doc,ModelPathUtils.ConvertUserVisiblePathToModelPath(path),new RevitLinkOptions(false));
                        RevitLinkInstance.Create(doc,type.ElementId);var rotated=RevitLinkInstance.Create(doc,type.ElementId);
                        ElementTransformUtils.RotateElement(doc,rotated.Id,Line.CreateBound(XYZ.Zero,XYZ.BasisZ),Math.PI/4);tx.Commit();
                    }
                    DocumentSession.Activate(doc);
                    using(var defaultJob=new ClashResolveAI.ClashEngine.ClashEngine(doc).CreateJob(null,true)){
                        while(!defaultJob.Advance(25)){}
                        Check(defaultJob.Statistics.TotalSources==1&&!defaultJob.Statistics.IncludeLinkToLink,"Unspecified job scope uses the persisted host-centric setting");
                    }
                    VerifyHostScopeExperiment(doc,_folder,"phase3-fixture",true);
                }finally {doc.Close(false);}
            }finally {AppSettings.Save(settings);}
        }
        internal static void VerifyHostScopeExperiment(Document doc,string folder,string name,bool fixture=false)
        {
            int links=new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Count(l=>l.GetLinkDocument()!=null);
            if(links==0){File.WriteAllText(Path.Combine(folder,name+".json"),JsonConvert.SerializeObject(new {executed=false,reason="No loaded links in the guarded production copy. No production indexing winner adopted."},Formatting.Indented));return;}
            var measurements=new List<object>();
            foreach(var mode in new[]{ScanMode.HardOnly,ScanMode.HardAndClearance}){
                List<ClashResult>? expected=null,allRows=null;
                int allSources=0,hostSources=0;
                // Reverse the two host-centric methods on the second round to expose warm-up effects.
                foreach(var method in new[]{"all-octree","host-octree","host-native","host-native","host-octree"}){
                    bool all=method=="all-octree",native=method=="host-native";
                    GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
                    using var process=Process.GetCurrentProcess();process.Refresh();
                    long before=process.PrivateMemorySize64,peak=before;var timer=Stopwatch.StartNew();
                    var engine=new ClashResolveAI.ClashEngine.ClashEngine(doc);
                    using var job=engine.CreateJob(null,true,mode:mode,includeLinkToLink:all,nativeLinkQueries:native);
                    while(!job.Advance(25)){process.Refresh();peak=Math.Max(peak,process.PrivateMemorySize64);}
                    timer.Stop();process.Refresh();peak=Math.Max(peak,process.PrivateMemorySize64);
                    var rows=job.Results;var stats=job.Statistics;
                    if(all){allRows=rows;expected=rows.Where(c=>string.IsNullOrEmpty(c.LinkInstanceA)||string.IsNullOrEmpty(c.LinkInstanceB)).ToList();allSources=stats.TotalSources;}
                    else hostSources=stats.TotalSources;
                    string Fingerprint(ClashResult c)=>JsonConvert.SerializeObject(new {c.ClashId,c.TestType,c.UnverifiedReason,c.OverlapVolumeMM3,c.GapMM});
                    bool match=all||new HashSet<string>(expected!.Select(Fingerprint)).SetEquals(rows.Select(Fingerprint));
                    if(!match)throw new InvalidOperationException(name+" host pair agreement failed: "+method+" / "+mode);
                    if(!all&&rows.Any(c=>!string.IsNullOrEmpty(c.LinkInstanceA)&&!string.IsNullOrEmpty(c.LinkInstanceB)))throw new InvalidOperationException("Linked-only pair leaked into host-centric results.");
                    if(fixture&&all)Check(rows.Count>expected!.Count,"Loaded fixture includes linked-only pairs in "+mode);
                    if(fixture&&!all&&method=="host-native"){
                        var linkedOnly=allRows!.Where(c=>!string.IsNullOrEmpty(c.LinkInstanceA)&&!string.IsNullOrEmpty(c.LinkInstanceB)).ToList();
                        var original=linkedOnly.Select(c=>c.Status).ToList();
                        var merged=ResultLifecycle.Merge(allRows!,rows,mode,stats.Scope.Contains);
                        Check(linkedOnly.Select(c=>c.Status).SequenceEqual(original)&&linkedOnly.All(c=>merged.Rows.Contains(c)),"Host-centric merge retains out-of-scope linked-only rows in "+mode);
                    }
                    measurements.Add(new {mode=mode.ToString(),method,wallSeconds=timer.Elapsed.TotalSeconds,privateBytesBefore=before,sampledPeakPrivateBytes=peak,privateBytesAfter=process.PrivateMemorySize64,statistics=stats,match,hard=rows.Count(c=>c.TestType==ClashTestType.HardClash),clearance=rows.Count(c=>c.TestType==ClashTestType.ClearanceClash),unverified=rows.Count(c=>c.TestType==ClashTestType.Unverified),hostResults=all?expected!.Count:rows.Count,results=rows.Select(c=>new {c.ClashId,c.LinkInstanceA,c.LinkInstanceB,c.TestType,c.UnverifiedReason})});
                }
                if(fixture){Check(hostSources==1&&allSources==5,"Host-centric source count reduces from five to one in "+mode);Check(true,"Host-centric octree and native queries match all host ClashIds and classifications in "+mode);}
            }
            File.WriteAllText(Path.Combine(folder,name+".json"),JsonConvert.SerializeObject(new {executed=true,fixture,loadedLinkInstances=links,memoryMethod="Process private bytes sampled between 25 ms API slices; includes Revit caches, not isolated index allocation.",measurements},Formatting.Indented));
        }
    }
}
