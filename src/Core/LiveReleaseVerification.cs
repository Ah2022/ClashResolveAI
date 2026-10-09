using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.LiveMonitor;
using System;
using System.IO;
using System.Linq;

namespace ClashResolveAI.Core
{
    // Opt-in, disposable models only. Does not open the user's project.
    internal static partial class IntegrationVerification
    {
        private static ClashResolveAI.ClashEngine.ScanStatistics? _releasePrevious;
        private static LiveClashDto? _releaseDto;
        private static long _releaseGeneration;
        private static int _releaseResumeCount;
        private static string _releaseHost="";
        private static ElementId? _releasePipeA,_releasePipeB;
        private static void ReleaseStep(UIApplication app)
        {
            var service=LiveMonitorService.Instance;var radar=RadarDataStore.Instance;
            void Next(int step){_step=step;_next=DateTime.UtcNow.AddSeconds(3);}
            void Move(double x){using(var tx=new Transaction(_test!,"Release fixture move")){tx.Start();ElementTransformUtils.MoveElement(_test!,_moving!,new XYZ(x,0,0));tx.Commit();}}
            void Mode(MonitorMode mode){service.RequestMode(mode,DocumentSession.CurrentKey,service.SessionGeneration(DocumentSession.CurrentKey));service.ExecuteQueued(app);}
            if(ScanCoordinator.Busy){if(DateTime.UtcNow-_next>TimeSpan.FromMinutes(2))throw new InvalidOperationException("Full Scan timed out");return;}
            if(service.ScanStatus?.Outcome==LiveScanOutcome.ProtectionPaused&&_step!=706){
                if(++_releaseResumeCount>3)throw new InvalidOperationException("Repeated responsiveness protection pauses");
                Check(service.ScanStatus.QueueDepth>0,"Responsiveness protection preserves unfinished native work");
                service.RequestScan(LiveScanAction.Ledger);service.ExecuteQueued(app);_next=DateTime.UtcNow.AddSeconds(3);return;
            }
            if(_step==700)
            {
                Check(app.ActiveUIDocument==null,"Release verification begins without an open user model");
                var settings=AppSettings.Load().ScanSnapshot();settings.ShowToast=false;settings.ScanLinkedModels=true;settings.LiveMaximumSliceMilliseconds=100;AppSettings.Save(settings);
                var linked=app.Application.NewProjectDocument(UnitSystem.Metric);
                using(var tx=new Transaction(linked,"Release linked fixture")){tx.Start();Box(linked,BuiltInCategory.OST_StructuralColumns,XYZ.Zero);tx.Commit();}
                string linkPath=Path.Combine(_folder,"release-link.rvt");linked.SaveAs(linkPath,new SaveAsOptions {OverwriteExistingFile=true});linked.Close(false);
                var doc=app.Application.NewProjectDocument(UnitSystem.Metric);
                using(var tx=new Transaction(doc,"Release host fixture")){
                    tx.Start();_moving=Box(doc,BuiltInCategory.OST_MechanicalEquipment,new XYZ(.5,.5,0)).Id;Box(doc,BuiltInCategory.OST_StructuralColumns,XYZ.Zero);
                    var type=RevitLinkType.Create(doc,ModelPathUtils.ConvertUserVisiblePathToModelPath(linkPath),new RevitLinkOptions(false));RevitLinkInstance.Create(doc,type.ElementId);RevitLinkInstance.Create(doc,type.ElementId);tx.Commit();
                }
                _releaseHost=Path.Combine(_folder,"release-host.rvt");doc.SaveAs(_releaseHost,new SaveAsOptions {OverwriteExistingFile=true});doc.Close(false);
                _test=app.OpenAndActivateDocument(_releaseHost).Document;DocumentSession.Activate(_test);
                var warm=new ClashResolveAI.ClashEngine.ClashEngine(_test);Check(warm.RunTargetedScanWithLinks(new[]{_moving!}).Count>=3,"Native affected-source scan finds host and repeated loaded-link overlaps");
                Next(701);return;
            }
            if(_step==701){Check(!ScanCoordinator.Busy,"Live starts without running Full Scan on the fixture");service.Start(app);app.ActiveUIDocument.Selection.SetElementIds(new[]{_moving!});service.CheckCurrentSelection();Next(702);return;}
            if(_step==702){
                Check(app.GetDockablePane(ClashRadarPanel.PaneId).IsShown(),"Registered Radar dockable pane opens in native Revit");
                Check(radar.ActiveCount>=3&&radar.GetActive().All(c=>c.Verification==LiveVerificationState.Verified),"Live gateway publishes verified host and loaded-link DTOs");
                _releaseDto=radar.GetActive().First();ClashRadarPanel.Instance.ViewModel.Selected=_releaseDto;
                Check(LiveClashResolver.Resolve(_test!,_releaseDto).ElementA.IsValidObject,"Native resolver validates UniqueIds and geometry revisions");
                var scene=Inspection.InspectionGeometry.Build(_test!,LiveClashResolver.Resolve(_test!,_releaseDto),20);
                Check(scene.Meshes.Count>=2,"Native inspection resolves and captures current geometry");
                service.RequestNavigation(_releaseDto,NavMode.View3D);service.ExecuteQueued(app);Next(703);return;
            }
            if(_step==703){
                Check(app.ActiveUIDocument.ActiveView is View3D,"Queued live navigation opens the native clash view");
                service.RequestPane(false,DocumentSession.CurrentKey,service.SessionGeneration(DocumentSession.CurrentKey));service.ExecuteQueued(app);Check(!app.GetDockablePane(ClashRadarPanel.PaneId).IsShown(),"Radar hides without ending its monitor session");service.RewirePanel();service.ExecuteQueued(app);
                Check(app.GetDockablePane(ClashRadarPanel.PaneId).IsShown(),"Radar reopens the same registered pane");
                Mode(MonitorMode.Trigger);_releasePrevious=service.LastStatistics;Move(100);Next(704);return;
            }
            if(_step==704){Check(service.Mode==MonitorMode.Trigger&&ReferenceEquals(_releasePrevious,service.LastStatistics)&&service.ScanStatus!.QueueDepth>0,"Trigger retains committed changes without automatically scanning");service.RequestScan(LiveScanAction.Ledger);service.ExecuteQueued(app);Next(705);return;}
            if(_step==705){
                Check(radar.ActiveCount==0&&service.ScanStatus!.QueueDepth==0,"Trigger Check Changes resolves overlaps after movement");
                var trace=LiveDiagnostics.Current(DocumentSession.CurrentKey);
                Check(trace.DocumentChangedUtc!=null&&trace.BatchCreatedUtc!=null&&trace.ScanStartedUtc!=null&&trace.ScanCompletedUtc!=null&&trace.TestedPairCount>=0,"Native live batches record correlated timestamps and pair counters");
                Mode(MonitorMode.Off);_releasePrevious=service.LastStatistics;Move(-100);Next(706);return;
            }
            if(_step==706){Check(service.Mode==MonitorMode.Off&&service.ScanStatus!.QueueDepth==0&&ReferenceEquals(_releasePrevious,service.LastStatistics),"Off ignores edits and creates no live scan work");Mode(MonitorMode.Live);Move(.1);Next(707);return;}
            if(_step==707){Check(radar.ActiveCount>=3&&service.ScanStatus!.QueueDepth==0,"Returning to Live checks subsequent committed edits automatically");_releaseDto=radar.GetActive().First();using(var tx=new Transaction(_test!,"Release delete")){tx.Start();_test!.Delete(_moving!);tx.Commit();}Next(708);return;}
            if(_step==708){Check(radar.ActiveCount==0,"Confirmed native deletion resolves old live clashes");using(var tx=new Transaction(_test!,"Release placement")){tx.Start();_moving=Box(_test!,BuiltInCategory.OST_MechanicalEquipment,new XYZ(.5,.5,0)).Id;tx.Commit();}Next(709);return;}
            if(_step==709){
                Check(radar.ActiveCount>=3,"New native placement checks host and loaded links without selection");_releaseDto=radar.GetActive().First();_releaseGeneration=service.SessionGeneration(DocumentSession.CurrentKey);
                using(var tx=new Transaction(_test!,"Release global input")){tx.Start();new FilteredElementCollector(_test!).OfClass(typeof(Level)).FirstElement().Name="Release changed level";tx.Commit();}Next(710);return;
            }
            if(_step==710){
                Check(!service.ScanStatus!.RequiresFullScan&&!ScanCoordinator.Busy&&ScanCoordinator.InputsStale&&radar.GetActive().Any(c=>c.Verification==LiveVerificationState.Verified),"Global input edit rechecks Radar independently while Full Scan remains stale");
                long stale=LiveDiagnostics.Current(DocumentSession.CurrentKey).StaleRequestCount;service.RequestNavigation(_releaseDto!,NavMode.View3D);service.RequestInspection(_releaseDto!);service.ExecuteQueued(app);
                Check(LiveDiagnostics.Current(DocumentSession.CurrentKey).StaleRequestCount>=stale+2,"Stale navigation and inspection are rejected and counted");
                Move(.1);Check(service.ScanStatus!.QueueDepth>0,"New local edits remain queued independently of Full Scan");Next(711);return;
            }
            if(_step==711){
                if(service.ScanStatus!.QueueDepth>0){if(DateTime.UtcNow-_next>TimeSpan.FromSeconds(30))throw new InvalidOperationException("Full Scan resume timed out");return;}
                Check(ScanCoordinator.InputsStale&&service.SessionGeneration(DocumentSession.CurrentKey)==_releaseGeneration,"Live completes its own checks without certifying Full Scan inputs or changing session generation");
                Check(radar.ActiveCount>=3&&radar.GetActive().All(c=>c.SessionGeneration==service.SessionGeneration(DocumentSession.CurrentKey)),"Resumed Radar rows are freshly validated in the new generation");
                _releaseDto=radar.GetActive().First(c=>c.LinkInstanceA!=""||c.LinkInstanceB!="");
                using(var tx=new Transaction(_test!,"Release move link")){tx.Start();ElementTransformUtils.MoveElement(_test!,new FilteredElementCollector(_test!).OfClass(typeof(RevitLinkInstance)).FirstElementId(),new XYZ(10,0,0));tx.Commit();}Next(712);return;
            }
            if(_step==712){
                Check(!service.ScanStatus!.RequiresFullScan&&!ScanCoordinator.Busy,"Native link transform changes are handled by local Radar rechecks");bool rejected=false;try{LiveClashResolver.Resolve(_test!,_releaseDto!);}catch(InvalidOperationException){rejected=true;}Check(rejected,"A moved loaded link invalidates its old DTO identity/environment");
                using(var tx=new Transaction(_test!,"Independent Radar temporary crossing pipes")){
                    tx.Start();_releasePipeA=NativePipe(_test!,new XYZ(495,0,3),new XYZ(505,0,3)).Id;
                    _releasePipeB=NativePipe(_test!,new XYZ(500,-5,3),new XYZ(500,5,3)).Id;tx.Commit();
                }
                Next(713);return;
            }
            bool PipePair(LiveClashDto c)=>(c.ElementAId==_releasePipeA?.Value&&c.ElementBId==_releasePipeB?.Value)||(c.ElementBId==_releasePipeA?.Value&&c.ElementAId==_releasePipeB?.Value);
            if(_step==713){
                if(service.ScanStatus!.QueueDepth>0){if(DateTime.UtcNow-_next>TimeSpan.FromSeconds(30))throw new InvalidOperationException("Pipe Radar detection timed out");return;}
                Check(radar.GetActive().Any(c=>PipePair(c)&&c.TestType==ClashTestType.HardClash&&c.Verification==LiveVerificationState.Verified),"Temporary crossing pipes publish a verified hard clash only to Radar without Full Scan");
                Check(!ScanCoordinator.Busy&&Dashboard.ClashDashboard.Instance.Clashes.Count==0,"Live pipe test does not start Full Scan or populate Dashboard");
                using(var tx=new Transaction(_test!,"Independent Radar move pipe clear")){tx.Start();ElementTransformUtils.MoveElement(_test!,_releasePipeB!,new XYZ(0,0,1));tx.Commit();}
                Next(714);return;
            }
            if(_step==714){
                if(service.ScanStatus!.QueueDepth>0){if(DateTime.UtcNow-_next>TimeSpan.FromSeconds(30))throw new InvalidOperationException("Pipe Radar resolution timed out");return;}
                Check(!radar.GetActive().Any(PipePair),"Moving the temporary pipe clear resolves its Radar clash automatically");
                using(var tx=new Transaction(_test!,"Independent Radar remove temporary pipes")){tx.Start();_test!.Delete(_releasePipeA!);_test.Delete(_releasePipeB!);tx.Commit();}
                Check(_test!.GetElement(_releasePipeA!)==null&&_test.GetElement(_releasePipeB!)==null,"Temporary pipe test elements are removed");
                service.Stop();Diagnostics.FlushLive(TimeSpan.FromSeconds(2));
                Check(File.Exists(Path.Combine(_folder,"diagnostics","live-monitor.jsonl")),"Structured diagnostic log is written outside the API callback");
                File.WriteAllText(Path.Combine(_folder,"complete.txt"),"PASS "+Results.Count+" native release checks; version 9.4.0. Undo/Redo and large production-model acceptance require separate verification.");
                if(app.Application.Documents.Cast<Document>().Where(d=>!d.IsLinked).All(d=>d.PathName.StartsWith(_folder,StringComparison.OrdinalIgnoreCase)))File.WriteAllText(Path.Combine(_folder,"safe-to-close.txt"),System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                _timer?.Stop();
            }
        }
    }
}
